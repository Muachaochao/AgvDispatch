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
            request.MapId != map.MapId || request.MapVersionId != map.VersionId || request.State is not ("Acquired" or "Held"))
            throw new InvalidDataException("发送路线与已取得路权的身份/地图不一致。");
        var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(payloadJson, JsonOptions)
            ?? throw new InvalidDataException("发送载荷无效。");
        var edges = JsonSerializer.Deserialize<string[]>(request.EdgeIds) ?? [];
        if (payload.BusinessTaskId != stepId || payload.VehicleId != vehicleId || payload.Path is null ||
            payload.Path.Count < 2 || edges.Length != payload.Path.Count - 1 ||
            payload.Source != payload.Path[0] || payload.Destination != payload.Path[^1])
            throw new InvalidDataException("发送路径与原步骤/有序路段不一致。");
        var nodes = payload.Path.Select(site =>
        {
            var matches = map.Definition.Nodes.Where(n => n.Enabled && string.Equals(
                string.IsNullOrWhiteSpace(n.AgvSiteId) ? n.Id : n.AgvSiteId, site, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("发送站点在活动地图中不是唯一有效节点。");
            return matches[0].Id;
        }).ToArray();
        if (MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, request.Source) != nodes[0] ||
            MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, request.Destination) != nodes[^1])
            throw new InvalidDataException("发送路径起终点与已取得路权不一致。");
        for (var i = 0; i < edges.Length; i++)
        {
            var matches = map.Definition.Edges.Where(e => e.Id == edges[i]).ToArray();
            if (matches.Length != 1 || !matches[0].Enabled || !(matches[0].From == nodes[i] && matches[0].To == nodes[i+1] ||
                matches[0].Bidirectional && matches[0].To == nodes[i] && matches[0].From == nodes[i+1]))
                throw new InvalidDataException("发送路径有序路段断链、禁用或方向不符。");
        }
        return new(1,taskId,stepId,commandId,vehicleId,map.MapId,map.VersionId,map.Definition.CoordinateSystem,
            Hash(JsonSerializer.Serialize(map.Definition, JsonOptions)),
            Hash(payloadJson),nodes,edges,payload.Path.ToArray());
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
