using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class ExecutionRecoveryService
{
    private sealed record ChargingBootOwnership(ChargingSession Session, TrafficResourceLeaseEntity[] Leases);

    private async Task<Observation> ObserveChargingAsync(ExecutionRecoveryTicketEntity ticket, ChargingSession charging,
        IReadOnlyList<AgvCommandOutboxEntity> commands, IReadOnlyList<TrafficResourceLeaseEntity> leases, CancellationToken ct)
    {
        var audits = await database.Queryable<ExecutionEvidenceEntity>().Where(e => e.SessionId == _sessionId &&
            e.TaskId == ticket.TaskId && e.Kind == "ChargingBootOwnership").ToListAsync(ct);
        ChargingBootOwnership? original = null;
        try { if (audits.Count == 1) original = JsonSerializer.Deserialize<ChargingBootOwnership>(audits[0].Evidence); }
        catch (JsonException) { }
        var hard = ChargingOwnershipError(ticket.TaskId, original, charging);
        if (commands.Count != 0) hard ??= "充电归属下出现业务Outbox命令，不能按独立充电会话核定关闭。";
        if (leases.Count != 0) hard ??= "充电会话仍持有原充电位或道路；请在充电恢复入口核对停止并实际离位，本页不释放资源。";
        if (charging.Phase != ChargingPhase.Completed) hard ??= "充电会话尚未完成现场核对关闭；请从充电恢复入口处理原会话。";
        if (charging.Phase == ChargingPhase.Completed && (charging.UpdatedAt < _started ||
            charging.UpdatedAt > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1) || string.IsNullOrWhiteSpace(charging.Operator) || string.IsNullOrWhiteSpace(charging.OperatorEvidence)))
            hard ??= "未取得本次启动后带操作人及现场依据的充电关闭记录。";
        if (charging.Phase == ChargingPhase.Completed && (charging.Battery is not { Charging: false, Current: { } current } ||
            !double.IsFinite(current) || charging.Battery.ObservedAt < _started || charging.Battery.ObservedAt > charging.UpdatedAt.AddSeconds(1)))
            hard ??= "充电关闭记录缺少本次启动后的停止充电反馈。";
        vehicles.TryGet(charging.VehicleId, out var vehicle);
        if (charging.Phase == ChargingPhase.Completed)
        {
            hard ??= VehicleBlock(vehicle, true);
            if (vehicle is not null && (vehicle.Charging != false || vehicle.Tasks.Any(t => t.Status is not (4 or 5 or 6 or 7))))
                hard ??= "车辆仍在充电、充电状态未知或有未结束车端任务。";
            if (charging.Route is { } route && charging.Site is { } site)
            {
                var active = map.Current;
                if (active?.MapId != route.MapId || active.VersionId != route.MapVersionId)
                    hard ??= "充电关闭记录与当前运行地图版本不一致。";
                else
                {
                    var exits = active.Definition.Nodes.Where(n =>
                        n.Enabled && MapRouteSiteMapper.IsSameNode(active.Definition, n.Id, site.ExitSiteId)).ToArray();
                    if (exits.Length != 1 || vehicle is null || string.IsNullOrWhiteSpace(vehicle.CurrentStation) ||
                        !MapRouteSiteMapper.IsSameNode(active.Definition, vehicle.CurrentStation, exits[0].Id) ||
                        vehicle.X is not { } x || vehicle.Y is not { } y ||
                        Math.Sqrt(Math.Pow(x - exits[0].X, 2) + Math.Pow(y - exits[0].Y, 2)) > settings.MaximumStationDeviationMeters)
                        hard ??= "充电关闭后车辆实际位置与原退出点不一致，不能核定离位。";
                }
            }
        }
        var identity = new RecoveryIdentityVerification(hard is null ? "ChargingClosed" : "ChargingHold",
            hard ?? "原充电会话已按本次现场核对关闭，原租约归属与停止离位记录一致。", hard);
        var reason = hard ?? "已核对：原充电会话已由现场人员确认停止并实际退出，充电位和道路已释放；不重发任何动作。";
        var evidence = JsonSerializer.Serialize(new { CapturedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(), Charging = charging,
            OriginalChargingOwnership = original, Leases = leases, Commands = commands, Vehicle = vehicle, Identity = identity });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { Charging = charging, OriginalChargingOwnership = original, Leases = leases, Commands = commands }))));
        return new(null, null, null, vehicle, null, hard is null, reason, hard, evidence, fingerprint, identity);
    }

    private static string? ChargingOwnershipError(string owner, ChargingBootOwnership? original, ChargingSession charging)
    {
        if (original is null || original.Session is not { Site: { } originalSite, Route: { } originalRoute } boot || original.Leases is not { Length: > 0 })
            return "缺少完整的本次启动充电归属快照，不能凭任务编号或资源消失自动放行。";
        if (owner != charging.SessionId || owner != boot.SessionId || charging.VehicleId != boot.VehicleId || charging.CreatedAt != boot.CreatedAt ||
            boot.Phase == ChargingPhase.Completed || charging.Site is null || charging.Route is null ||
            JsonSerializer.Serialize(charging.Site) != JsonSerializer.Serialize(originalSite) || JsonSerializer.Serialize(charging.Route) != JsonSerializer.Serialize(originalRoute))
            return "持久充电会话与本次启动保存的车辆、充电位或原地图路线不一致。";
        if (original.Leases.Any(l => l.TaskId != owner || l.VehicleId != boot.VehicleId || l.OwnerStepId != owner + "-LOCK" ||
            l.MapId != originalRoute.MapId || l.MapVersionId != originalRoute.MapVersionId) ||
            original.Leases.Select(l => l.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != original.Leases.Length ||
            !original.Leases.Select(l => l.ResourceId).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(originalRoute.ResourceIds) ||
            !originalRoute.ResourceIds.Contains(originalSite.ResourceId, StringComparer.OrdinalIgnoreCase))
            return "启动时原充电租约的步骤、车辆、地图或资源集合与充电记录不一致。";
        return null;
    }
}
