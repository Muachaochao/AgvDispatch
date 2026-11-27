using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed partial class TrafficResourceControlService
{
    public async Task<TrafficLifecycleSnapshot> GetLifecycleAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requests = await database.Queryable<TrafficRequestEntity>().OrderByDescending(r => r.UpdatedAt).Take(201).ToListAsync(ct);
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().ToListAsync(ct);
        var heldStepsMissingFromPage = leases.Select(l => l.OwnerStepId).Except(requests.Select(r => r.StepId)).Distinct().ToArray();
        if (heldStepsMissingFromPage.Length > 0)
            requests.AddRange(await database.Queryable<TrafficRequestEntity>().Where(r => heldStepsMissingFromPage.Contains(r.StepId)).ToListAsync(ct));
        foreach (var group in leases.Where(l => !requests.Any(r => r.StepId == l.OwnerStepId)).GroupBy(l => l.OwnerStepId))
        {
            var lease = group.First();
            requests.Add(new TrafficRequestEntity { StepId = lease.OwnerStepId, TaskId = lease.TaskId, VehicleId = lease.VehicleId,
                State = "Held", Reason = "旧占用无路径请求记录，显示实际资源关联；不反推历史路径。", ResourceIds = JsonSerializer.Serialize(group.Select(l => l.ResourceId)),
                RequestedAt = lease.AcquiredAt, UpdatedAt = lease.RenewedAt });
        }
        var taskIds = requests.Select(r => r.TaskId).ToArray();
        var tasks = taskIds.Length == 0 ? [] : await database.Queryable<DispatchTaskEntity>().Where(t => taskIds.Contains(t.TaskId)).ToListAsync(ct);
        var nowUtc = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
        var byTask = tasks.ToDictionary(t => t.TaskId);
        var rows = requests.OrderByDescending(r => r.UpdatedAt).Take(200).Select(r => ProjectRequest(r, leases, byTask, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime, nowUtc)).ToArray();
        var events = await database.Queryable<TrafficLifecycleEventEntity>().OrderBy("rowid DESC").Take(100).ToListAsync(ct);
        return new(nowUtc, rows, events.Select(e => new TrafficLifecycleEvent(e.EventId, e.StepId, e.TaskId, e.VehicleId,
            e.Kind, e.Reason, JsonSerializer.Deserialize<string[]>(e.ResourceIds) ?? [], new(e.OccurredAt)) { WaitingSince = e.WaitingSinceUtc == null ? null : new DateTimeOffset(DateTime.SpecifyKind(e.WaitingSinceUtc.Value,DateTimeKind.Utc)), WaitingStartKnown = e.WaitingStartKnown == true }).ToArray(), requests.Count > 200);
    }
    private TrafficRequestSnapshot ProjectRequest(TrafficRequestEntity r, IReadOnlyList<TrafficResourceLeaseEntity> leases,
        IReadOnlyDictionary<string, DispatchTaskEntity> tasks, DateTime now, DateTimeOffset nowUtc)
    {
        var resourceIds = JsonSerializer.Deserialize<string[]>(r.ResourceIds) ?? [];
        var own = leases.Where(l => l.OwnerStepId == r.StepId && l.TaskId == r.TaskId).ToArray();
        var blockingLeases = TrafficBlockageObservation.Find(resourceIds,r.TaskId,leases,catalog);
        var blockers = blockingLeases
            .Select(l => new TrafficBlocker(l.ResourceId, l.TaskId, l.VehicleId, l.OwnerStepId, l.ExpiresAt < now)
            { MapId = l.MapId, MapVersionId = l.MapVersionId }).ToArray();
        var task = tasks.GetValueOrDefault(r.TaskId);
        var stage = task?.Stage ?? -1;
        var state = own.Length > 0 ? stage is 100 or 110 or 120 or 130 or -1 ? "Retained" : r.State == "Waiting" ? "Waiting" : "Held" :
            r.State == "Released" ? "Released" : stage is 90 or 100 or 110 or 120 or 130 or -1 ? "Inactive" :
            r.State is "Acquired" or "Held" ? "NoLease" : r.State;
        var reason = state == "Retained" ? "原任务异常、取消核对或任务缺失；仍保留占用，须按现场处置流程核对。" :
            state == "NoLease" ? "当前无对应占用，请结合人工解锁和路权历史核对。" :
            state == "Waiting" && (r.WaitingCategory is null or "Route") && blockers.Length == 0 && resourceIds.Length > 0 ?
                r.QueuePriority != null ? "当前未观察到资源容量占满，等待滚动调度按有效优先级和呼叫时间重新申请；已有路权保持不变，查询不授予路权。" :
                "上次资源冲突已不在当前占用中，等待下一次调度重新规划申请；不会在查询时自动取得路权。" : r.Reason;
        var seconds = state == "Waiting" && r.WaitingSinceUtc != null && nowUtc.UtcDateTime >= r.WaitingSinceUtc.Value ? (double?)(nowUtc.UtcDateTime-r.WaitingSinceUtc.Value).TotalSeconds : null;
        var clockValid = r.WaitingSinceUtc == null || nowUtc.UtcDateTime >= r.WaitingSinceUtc.Value;
        var alerts = waitingAlerts ?? new TrafficWaitingAlertOptions();
        return new TrafficRequestSnapshot(r.StepId, r.TaskId, r.VehicleId, r.Source, r.Destination,
            JsonSerializer.Deserialize<string[]>(r.EdgeIds) ?? [], resourceIds, state, reason, blockers,
            new(r.RequestedAt), new(r.UpdatedAt)) { WaitingCategory = r.WaitingCategory ?? "Route", Priority = task?.Priority,
            WaitingSince = state == "Waiting" && r.WaitingSinceUtc != null ? new DateTimeOffset(DateTime.SpecifyKind(r.WaitingSinceUtc.Value, DateTimeKind.Utc)) : null,
            WaitingStartKnown = r.WaitingStartKnown == true,
            WaitingAlertLevel = alerts.Evaluate(state,seconds,clockValid), WaitingWarningSeconds=alerts.WarningSeconds, WaitingCriticalSeconds=alerts.CriticalSeconds,
            BlockingObservationCurrent = blockingLeases.Length>0 && r.BlockingKey==TrafficBlockageObservation.Key(blockingLeases),
            BlockingSince = blockingLeases.Length>0 && r.BlockingKey==TrafficBlockageObservation.Key(blockingLeases) && r.BlockingSinceUtc!=null ? new DateTimeOffset(DateTime.SpecifyKind(r.BlockingSinceUtc.Value,DateTimeKind.Utc)) : null,
            BlockingStartKnown = r.BlockingStartKnown==true,
            BlockingClockValid = r.BlockingSinceUtc==null || nowUtc.UtcDateTime>=r.BlockingSinceUtc.Value,
            LastBlockingClearedAt = r.LastBlockingClearedUtc==null ? null : new DateTimeOffset(DateTime.SpecifyKind(r.LastBlockingClearedUtc.Value,DateTimeKind.Utc)),
            WaitingClockValid = r.WaitingSinceUtc == null || nowUtc.UtcDateTime >= r.WaitingSinceUtc.Value,
            WaitingSeconds = state == "Waiting" && r.WaitingSinceUtc != null && nowUtc.UtcDateTime >= r.WaitingSinceUtc.Value ? (nowUtc.UtcDateTime - r.WaitingSinceUtc.Value).TotalSeconds : null,
            MapId = r.MapId, MapVersionId = r.MapVersionId, HeldResources = own.Select(l => new TrafficHeldResource(l.ResourceId,l.MapId,l.MapVersionId)).ToArray(), HeldResourceIds = own.Select(l => l.ResourceId).Distinct().ToArray() };
    }

}
