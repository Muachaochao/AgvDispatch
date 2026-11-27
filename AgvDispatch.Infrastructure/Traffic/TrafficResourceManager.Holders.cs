using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceManager
{
    private static readonly JsonSerializerOptions HolderJson = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<int> TransportStepTypes =
    [
        (int)DispatchTaskStepType.GoToPickup,
        (int)DispatchTaskStepType.DeliverFullBoxToDumper,
        (int)DispatchTaskStepType.EnterDumpingPosition,
        (int)DispatchTaskStepType.ReturnEmptyBox
    ];

    /// <summary>
    /// B07-R8: describes who holds the requested resources and what each holder may still
    /// use on its current transport step: every resource of the sent window and of all later
    /// windows of that step, plus a pending traffic request. Read only; no lease changes.
    /// </summary>
    public async Task<IReadOnlyList<TrafficBlockingHolder>?> GetBlockingHoldersAsync(string requestingTaskId,
        IReadOnlyCollection<string> resourceIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingTaskId);
        if (resourceIds.Count == 0) return [];
        var wanted = resourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leases = (await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(lease => lease.TaskId != requestingTaskId)
                .ToListAsync(cancellationToken))
            .Where(lease => wanted.Contains(lease.ResourceId))
            .ToList();
        var holders = new List<TrafficBlockingHolder>();
        foreach (var group in leases.GroupBy(lease => (lease.TaskId, lease.OwnerStepId, lease.VehicleId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocked = group.Select(lease => lease.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            TrafficBlockingHolder Unknown(string reason) =>
                new(group.Key.VehicleId ?? "", group.Key.TaskId, group.Key.OwnerStepId, false, blocked, [], null, reason);
            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item => item.StepId == group.Key.OwnerStepId).FirstAsync(cancellationToken);
            if (step is null || !TransportStepTypes.Contains(step.StepType) ||
                step.Status != (int)DispatchTaskStepStatus.Executing || string.IsNullOrWhiteSpace(step.VendorTaskId))
            {
                holders.Add(Unknown("占用方不是执行中的运输步骤（充电、人工、停车或收尾占用）。"));
                continue;
            }
            var commands = await database.Queryable<AgvCommandOutboxEntity>()
                .Where(item => item.StepId == step.StepId && item.CommandType == (int)AgvCommandType.Transport &&
                    item.VendorCommandId == step.VendorTaskId)
                .ToListAsync(cancellationToken);
            if (commands.Count != 1)
            {
                holders.Add(Unknown("占用方当前运输命令缺失或不唯一。"));
                continue;
            }
            AgvTransportCommandPayload? payload;
            try { payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(commands[0].PayloadJson, HolderJson); }
            catch (JsonException) { payload = null; }
            if (payload is null || payload.Windows.Count == 0 || payload.WindowIndex < 0 ||
                payload.WindowIndex >= payload.Windows.Count)
            {
                holders.Add(Unknown("占用方运输载荷无法确定剩余路线。"));
                continue;
            }
            var remaining = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in payload.Windows.Skip(payload.WindowIndex)) remaining.UnionWith(window.ResourceIds);
            var request = await database.Queryable<TrafficRequestEntity>()
                .Where(item => item.StepId == step.StepId).FirstAsync(cancellationToken);
            if (request is not null && !string.IsNullOrWhiteSpace(request.ResourceIds))
            {
                try { remaining.UnionWith(JsonSerializer.Deserialize<string[]>(request.ResourceIds) ?? []); }
                catch (JsonException) { holders.Add(Unknown("占用方路权申请记录无法解析。")); continue; }
            }
            holders.Add(new(group.Key.VehicleId ?? "", group.Key.TaskId, group.Key.OwnerStepId, true, blocked,
                remaining.Order(StringComparer.OrdinalIgnoreCase).ToArray(), payload.Windows[^1].Destination,
                "占用方处于运输步骤，已读取其当前及后续窗口。"));
        }
        return holders;
    }
}
