using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Traffic;

public sealed record TransportRouteSnapshot(int SchemaVersion, string TaskId, string StepId, string CommandId,
    string VehicleId, string MapId, string MapVersionId, string CoordinateFrameId, string MapDefinitionHash, string PayloadHash,
    string[] NodeIds, string[] EdgeIds, string[] AgvSites);

public static class TransportRouteSnapshots
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static TransportRouteSnapshot Capture(string taskId, string stepId, string commandId, string vehicleId,
        string payloadJson, ActiveMapRuntimeSnapshot map, TrafficRequestEntity request)
    {
        if (request.TaskId != taskId || request.StepId != stepId || request.VehicleId != vehicleId ||
            request.MapId != map.MapId || request.MapVersionId != map.VersionId || request.State is not ("Acquired" or "Held" or "NotRequired"))
            throw new InvalidDataException("发送路线与已取得路权的身份/地图不一致。");
        var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(payloadJson, JsonOptions)
            ?? throw new InvalidDataException("发送载荷无效。");
        var requestEdges = JsonSerializer.Deserialize<string[]>(request.EdgeIds) ?? [];
        var vendorPath = ResolveFullVendorPath(map.Definition, payload, requestEdges,
            request.Source, request.Destination, out var routeEdges);
        var businessStepId = string.IsNullOrWhiteSpace(payload.BusinessStepId)
            ? payload.BusinessTaskId
            : payload.BusinessStepId;
        if ((businessStepId != stepId && !ObstacleRecoveryTaskIds.IsRecoveryTask(businessStepId)) ||
            payload.VehicleId != vehicleId || payload.Path is null ||
            payload.Path.Count < 2 || routeEdges.Length != vendorPath.Length - 1 ||
            payload.Source != payload.Path[0] || payload.Destination != payload.Path[^1])
            throw new InvalidDataException("发送路径与原步骤/有序路段不一致。");
        var nodes = vendorPath.Select(site =>
        {
            if (!MapRouteSiteMapper.TryResolveNode(map.Definition, site, out var node) ||
                node is not { Enabled: true })
                throw new InvalidDataException("发送站点在活动地图中不是唯一有效节点。");
            return node.Id;
        }).ToArray();
        for (var i = 0; i < routeEdges.Length; i++)
        {
            var matches = map.Definition.Edges.Where(e => e.Id == routeEdges[i]).ToArray();
            if (matches.Length != 1 || !matches[0].Enabled)
                throw new InvalidDataException("发送路径有序路段断链、禁用或方向不符。");
            var forward = MapRouteSiteMapper.IsSameNode(
                              map.Definition, matches[0].From, nodes[i]) &&
                          MapRouteSiteMapper.IsSameNode(
                              map.Definition, matches[0].To, nodes[i + 1]);
            var reverse = matches[0].Bidirectional &&
                          MapRouteSiteMapper.IsSameNode(
                              map.Definition, matches[0].To, nodes[i]) &&
                          MapRouteSiteMapper.IsSameNode(
                              map.Definition, matches[0].From, nodes[i + 1]);
            if (!forward && !reverse)
                throw new InvalidDataException("发送路径有序路段断链、禁用或方向不符。");
        }
        if (request.State == "NotRequired" &&
            ((JsonSerializer.Deserialize<string[]>(request.ResourceIds) ?? []).Length != 0 ||
             map.Definition.Nodes.Where(n => nodes.Contains(n.Id)).Any(n => n.ResourceIds.Count > 0) ||
             map.Definition.Edges.Where(e => routeEdges.Contains(e.Id)).Any(e => e.ResourceIds.Count > 0)))
            throw new InvalidDataException("无需申请状态只能用于确实不经过任何交通资源的路径。");
        return new(1,taskId,stepId,commandId,vehicleId,map.MapId,map.VersionId,map.Definition.CoordinateSystem,
            // Match the Web map's published definition hash (string enums) so the task
            // page can prove a snapshot belongs to the active map version.
            map.DefinitionHash,
            Hash(payloadJson),nodes,routeEdges,vendorPath);
    }

    private static string[] ResolveFullVendorPath(MapDefinition definition,
        AgvTransportCommandPayload payload,
        string[] requestEdges, string requestSource, string requestDestination,
        out string[] routeEdges)
    {
        if (payload.Windows.Count == 0)
        {
            routeEdges = requestEdges;
            if (!MapRouteSiteMapper.IsSameNode(definition, requestSource, payload.Path[0]) ||
                !MapRouteSiteMapper.IsSameNode(definition, requestDestination, payload.Path[^1]))
                throw new InvalidDataException("发送路径起终点与已取得路权不一致。");
            return payload.Path.ToArray();
        }

        var windows = payload.Windows.OrderBy(item => item.Index).ToArray();
        if (windows.Length == 0 || windows.Select(item => item.Index).Distinct().Count() != windows.Length ||
            windows.Where((item, index) => item.Index != index).Any() ||
            payload.WindowIndex < 0 || payload.WindowIndex >= windows.Length ||
            payload.WindowCount < windows.Length)
            throw new InvalidDataException("滚动窗口索引或数量无效。");

        var fullPath = new List<string>();
        var fullEdges = new List<string>();
        foreach (var window in windows)
        {
            if (window.Path is null || window.Path.Count < 2 ||
                window.Path.Any(string.IsNullOrWhiteSpace) ||
                window.Source != window.Path[0] || window.Destination != window.Path[^1] ||
                window.EdgeIds is null || window.EdgeIds.Count != window.Path.Count - 1)
                throw new InvalidDataException("滚动窗口路径、端点或有序路段不一致。");
            if (fullPath.Count == 0)
            {
                fullPath.AddRange(window.Path);
            }
            else
            {
                if (!MapRouteSiteMapper.NormalizeSiteId(fullPath[^1])!
                        .Equals(MapRouteSiteMapper.NormalizeSiteId(window.Path[0]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("滚动窗口之间存在不连续断点。");
                fullPath.AddRange(window.Path.Skip(1));
            }
            fullEdges.AddRange(window.EdgeIds);
        }

        var current = windows[payload.WindowIndex];
        if (!payload.Path.SequenceEqual(current.Path, StringComparer.OrdinalIgnoreCase) ||
            !MapRouteSiteMapper.IsSameNode(definition, payload.Source, current.Source) ||
            !MapRouteSiteMapper.IsSameNode(definition, payload.Destination, current.Destination) ||
            !requestEdges.SequenceEqual(current.EdgeIds, StringComparer.OrdinalIgnoreCase) ||
            !MapRouteSiteMapper.IsSameNode(definition, requestSource, current.Source) ||
            !MapRouteSiteMapper.IsSameNode(definition, requestDestination, current.Destination))
            throw new InvalidDataException("当前发送载荷与选定的滚动窗口不一致。");
        routeEdges = fullEdges.ToArray();
        return fullPath.ToArray();
    }

    public static string Serialize(TransportRouteSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    public static TransportRouteSnapshot Read(AgvCommandOutboxEntity command)
    {
        if (string.IsNullOrWhiteSpace(command.ExecutionRouteSnapshotJson) || string.IsNullOrWhiteSpace(command.ExecutionRouteSnapshotHash))
            throw new InvalidDataException("原命令没有发送时路线快照，禁止从当前路径补造历史。");
        if (Hash(command.ExecutionRouteSnapshotJson) != command.ExecutionRouteSnapshotHash)
            throw new InvalidDataException("原路线快照摘要不一致。");
        var snapshot = JsonSerializer.Deserialize<TransportRouteSnapshot>(command.ExecutionRouteSnapshotJson, JsonOptions)
            ?? throw new InvalidDataException("原路线快照无效。");
        if (snapshot.SchemaVersion != 1 || snapshot.TaskId != command.TaskId || snapshot.StepId != command.StepId ||
            snapshot.CommandId != command.CommandId || snapshot.VehicleId != command.VehicleId ||
            snapshot.MapId != command.ExecutionMapId || snapshot.MapVersionId != command.ExecutionMapVersionId ||
            snapshot.PayloadHash != Hash(command.PayloadJson) || snapshot.NodeIds is null || snapshot.EdgeIds is null || snapshot.AgvSites is null)
            throw new InvalidDataException("原路线快照与命令归属/载荷不一致。");
        return snapshot;
    }

    public static void InstallImmutabilityGuard(ISqlSugarClient database)
    {
        // Null legacy snapshots stay null. Status/retry/vendor acknowledgement updates remain allowed.
        database.Ado.ExecuteCommand("""
            CREATE TRIGGER IF NOT EXISTS guard_agv_route_snapshot_update
            BEFORE UPDATE ON agv_command_outbox
            WHEN OLD.ExecutionRouteSnapshotJson IS NOT NEW.ExecutionRouteSnapshotJson
              OR OLD.ExecutionRouteSnapshotHash IS NOT NEW.ExecutionRouteSnapshotHash
              OR (OLD.ExecutionRouteSnapshotJson IS NOT NULL AND (
                OLD.CommandId IS NOT NEW.CommandId OR OLD.TaskId IS NOT NEW.TaskId OR OLD.StepId IS NOT NEW.StepId
                OR OLD.VehicleId IS NOT NEW.VehicleId OR OLD.PayloadJson IS NOT NEW.PayloadJson
                OR OLD.ExecutionMapId IS NOT NEW.ExecutionMapId OR OLD.ExecutionMapVersionId IS NOT NEW.ExecutionMapVersionId))
            BEGIN SELECT RAISE(ABORT, 'transport route snapshot is immutable'); END;
            """);
    }
}
