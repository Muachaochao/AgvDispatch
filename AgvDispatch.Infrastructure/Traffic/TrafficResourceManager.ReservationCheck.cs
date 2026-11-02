using System.Text.Json;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceManager
{
    public async Task<string?> CheckStepReservationAsync(string stepId, string taskId, string vehicleId,
        string? mapId, string? mapVersionId, string source, string destination, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ct.ThrowIfCancellationRequested();
        string? rejection = null;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var request = await database.Queryable<TrafficRequestEntity>().Where(r => r.StepId == stepId).FirstAsync(ct);
            if (request is null) { rejection = "发送前路权核验失败：缺少原步骤的持久化路径申请。"; return; }
            if (!string.Equals(request.TaskId, taskId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase))
            { rejection = "发送前路权核验失败：路径申请的任务或车辆归属不一致。"; return; }
            if (request.MapId != mapId || request.MapVersionId != mapVersionId)
            { rejection = "发送前路权核验失败：路径申请属于另一运行地图或版本。"; return; }
            if (!string.Equals(request.Source, source, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.Destination, destination, StringComparison.OrdinalIgnoreCase))
            { rejection = "发送前路权核验失败：路径申请起终点与待发送命令不一致。"; return; }
            if (request.State is not ("Acquired" or "Held" or "NotRequired"))
            { rejection = "发送前路权核验失败：原步骤尚未取得有效路权（当前状态：" + request.State + "）。"; return; }
            string[] resources;
            try { resources = JsonSerializer.Deserialize<string[]>(request.ResourceIds) ?? throw new JsonException(); }
            catch (JsonException) { rejection = "发送前路权核验失败：资源申请记录无法解析。"; return; }
            if (resources.Any(string.IsNullOrWhiteSpace) ||
                request.State == "NotRequired" && resources.Length != 0 ||
                request.State != "NotRequired" && resources.Length == 0)
            { rejection = "发送前路权核验失败：资源清单与路权状态不一致。"; return; }
            if (resources.Length == 0) return;
            var leases = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(l => l.OwnerStepId == stepId).ToListAsync(ct);
            foreach (var resource in resources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!catalog.TryGet(resource, out var definition) || definition.Capacity < 1 ||
                    !leases.Any(l => string.Equals(l.ResourceId, resource, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(l.TaskId, taskId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(l.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase) &&
                        l.MapId == mapId && l.MapVersionId == mapVersionId))
                { rejection = "发送前路权核验失败：资源" + resource + "缺少归属于原步骤、任务、车辆及地图版本的实际路权。"; return; }
            }
            // ExpiresAt is only heartbeat evidence. An expired timestamp never
            // invalidates an unreleased physical lease or makes its vehicle idle.
        });
        if (!transaction.IsSuccess) throw new InvalidOperationException("发送前核对持久化路权失败。", transaction.ErrorException);
        return rejection;
    }
}
