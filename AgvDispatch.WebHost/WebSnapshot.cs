using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.WebHost;

public static class WebJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
}

public sealed record WebMap(string? Id, string? Version, string Name, string Unit, string CoordinateSystem,
    object[] Nodes, object[] Edges, object[] Resources, string[] Warnings)
{public string DefinitionHash {get;init;}="";public IReadOnlyList<MapAreaAnnotation>? AreaAnnotations {get;init;}}

public sealed record WebSnapshot(DateTimeOffset ReceivedAt, bool Connected, string Source, string? Error,
    SystemStatusSnapshot? Status, WebMap? Map, DeviceMonitoringSnapshot? Devices,
    TaskListPage? Tasks, IReadOnlyList<TrafficResourceSnapshot>? Resources, string[] PositionedVehicleIds)
{
    public IReadOnlyList<AlarmSnapshot> Alarms { get; init; } = [];
    public TrafficLifecycleSnapshot? Traffic { get; init; }
}

public sealed class SnapshotReader(DispatchPipe pipe)
{
    public async Task<WebSnapshot> ReadAsync(CancellationToken ct)
    {
        // The production server handles pipe clients serially. Do not fan out connections.
        var status = await pipe.ReadAsync<SystemStatusSnapshot>(TrafficControlOperations.GetSystemStatus, new { }, ct)
            ?? throw new InvalidDataException("缺少系统状态。");
        var map = await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap, new TrafficMapQuery(), ct);
        var tasks = await pipe.ReadAsync<TaskListPage>(TrafficControlOperations.GetTasks, new TaskListQuery(PageSize: 50), ct);
        var devices = await pipe.ReadAsync<DeviceMonitoringSnapshot>(TrafficControlOperations.GetDeviceMonitoring, new { }, ct);
        var resources = await pipe.ReadAsync<TrafficResourceSnapshot[]>(TrafficControlOperations.GetResources, new { }, ct);
        var traffic = await pipe.ReadAsync<TrafficLifecycleSnapshot>(TrafficControlOperations.GetTrafficLifecycle, new { }, ct);
        var alarms = await pipe.ReadAsync<AlarmSnapshot[]>(TrafficControlOperations.GetAlarms, new { MaximumCount = 100, ActiveOnly = false }, ct);
        var settings = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct);
        // Multi-call observations are not an atomic device snapshot. Reject a map switch observed between calls.
        if (map?.MapId != status.Safety.ActiveMapId || map?.VersionId != status.Safety.ActiveMapVersionId ||
            settings?.RuntimeMapId != map?.MapId || settings?.RuntimeMapVersion != map?.VersionId)
            throw new InvalidOperationException("采集期间运行地图已变化，请等待下一次完整观察。");
        var boundPositions = settings?.Active.MapBindings?.Where(b => b.CoordinatesVerified &&
            b.LocalMapId == map?.MapId && b.LocalMapVersion == map?.VersionId &&
            !string.IsNullOrWhiteSpace(b.ConfirmationNote)).Select(b => b.VehicleId) ?? [];
        var positioned = ResolvePositionedVehicleIds(
            map?.Definition is { CoordinateSystem: "RobotWorld", Unit: "m" },
            map?.Definition?.Nodes.Where(node => node.Enabled)
                .Select(node => node.AgvSiteId).Where(site => !string.IsNullOrWhiteSpace(site))
                .Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [],
            boundPositions, status.Agvs);
        // Imported translated SMAP currently has no transform in the IPC contract. Do not put raw robot XY on it.
        if (map?.Definition?.CoordinateSystem == "SmapWorldTranslated") positioned = [];
        return new(DateTimeOffset.UtcNow, true, "backend", null, status, ProjectMap(map), devices, tasks, resources, positioned)
        { Alarms = alarms ?? [], Traffic = traffic };
    }

    public static string[] ResolvePositionedVehicleIds(bool robotWorldMeters,
        IReadOnlySet<string> mapStations, IEnumerable<string> businessBound,
        IEnumerable<AgvDiagnostic> agvs)
    {
        // A robot-world runtime map imported from the robot already carries the
        // matching coordinates; an additional business-map binding is not required.
        // Keep the safety gate tied to a live observation: online, good quality,
        // normal route position, and a reported station that exists in this map.
        var robotWorldPositions = robotWorldMeters
            ? agvs.Where(agv => agv.Connection == ConnectionState.Online && agv.Quality == SignalQuality.Good &&
                agv.MapPositionState == AgvMapPositionState.Normal && !string.IsNullOrWhiteSpace(agv.CurrentStation) &&
                mapStations.Contains(agv.CurrentStation)).Select(agv => agv.VehicleId)
            : [];
        return businessBound.Concat(robotWorldPositions)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static WebMap? ProjectMap(TrafficMapSnapshot? snapshot)
    {
        if (snapshot?.Definition is not { } definition) return null;
        if (definition.Nodes.Count > 5000 || definition.Edges.Count > 10000 || definition.Resources.Count > 2000)
            throw new InvalidDataException("地图超出本版 Web 显示预算（5000节点/10000道路/2000资源）。");
        var nodes = definition.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var edges = new List<object>();
        foreach (var edge in definition.Edges)
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to) ||
                !MapEdgeGeometry.TryValidate(edge, from, to, out _))
            { warnings.Add($"路段 {edge.Id} 几何不可用，未绘制。"); continue; }
            edges.Add(new { edge.Id, edge.From, edge.To, edge.Enabled, edge.Bidirectional,
                kind = edge.GeometryKind.ToString(), points = MapEdgeGeometry.Sample(edge, from, to, 32) });
        }
        if (definition.CoordinateSystem == "SmapWorldTranslated")
            warnings.Add("当前地图为 Smap 平移坐标；尚未取得车位变换，原始车位只在车辆面板显示。");
        if (definition.Unit != "m") warnings.Add("当前地图非米制，车位不叠加。");
        return new(snapshot.MapId, snapshot.VersionId, definition.Name, definition.Unit, definition.CoordinateSystem,
            definition.Nodes.Select(n => (object)new { n.Id, n.X, n.Y, n.AgvSiteId, n.Enabled, type = n.Type.ToString() }).ToArray(),
            edges.ToArray(), definition.Resources.Select(r => (object)new { r.Id, r.Capacity, r.Boundary }).ToArray(), warnings.ToArray()){AreaAnnotations=definition.AreaAnnotations,DefinitionHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition,new JsonSerializerOptions(JsonSerializerDefaults.Web)))))};
    }
}
