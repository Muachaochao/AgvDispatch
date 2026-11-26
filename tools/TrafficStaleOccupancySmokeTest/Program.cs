using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

// A - B - C, all bidirectional. A vehicle parked at B blocks the only way from A to C.
MapNode Node(string id, double x, double y, MapNodeType type) => new(id, x, y, type, true, [], id);
MapEdge Edge(string id, string from, string to) => new(id, from, to, 5, 1, true, true, []);
var definition = new MapDefinition("stale-occupancy", "", "LocalCartesian", "m",
    [Node("A", 0, 0, MapNodeType.Station), Node("B", 5, 0, MapNodeType.Waypoint), Node("C", 10, 0, MapNodeType.Station)],
    [Edge("AB", "A", "B"), Edge("BC", "B", "C")], []);
var runtime = new ActiveMapRuntime();
var now = DateTimeOffset.UtcNow;
runtime.Replace(new("MAP", "V1", definition, now));

AgvRobotSnapshot Robot(string id, string station, bool fresh) => new(id, id, "127.0.0.1", 19204,
    fresh ? ConnectionState.Online : ConnectionState.Interrupted, fresh ? SignalQuality.Good : SignalQuality.Bad,
    5, 0, 0, .95, station, station, 80, false, false, "Idle", null, null, null, [], [],
    fresh ? now : now.AddHours(-1), fresh ? now : now.AddHours(-1), 0, null, null, 1);

ConfiguredRoutePlanner Planner(AgvRobotSnapshot parked)
{
    var provider = new MapDefinitionProvider(new(), new MapImportService(), new MapValidationService());
    return new ConfiguredRoutePlanner(provider, runtime, new DispatchOrchestratorOptions(), new NoFlow(),
        null, new Vehicles(parked, Robot("V1", "A", true)), new MapOptions { MinimumLocalizationConfidence = .5 });
}

// Control: with no parked peer the route exists.
var free = Planner(Robot("V2", "C", true));
Check(free.PlanForVehicle("A", "C", new HashSet<string>(), "V1") is not null, "对照：无停车车辆时路线可以规划");

// Fresh telemetry: V2 is parked at B, so the route is blocked (existing behaviour).
var freshBlocked = Planner(Robot("V2", "B", true));
Check(freshBlocked.PlanForVehicle("A", "C", new HashSet<string>(), "V1") is null,
    "新鲜遥测的停车车辆阻塞所在站点及相邻路段");

// R4: the same parked vehicle with stale/offline telemetry still blocks its last known station.
var staleBlocked = Planner(Robot("V2", "B", false));
Check(staleBlocked.PlanForVehicle("A", "C", new HashSet<string>(), "V1") is null,
    "遥测过期或离线的停车车辆仍阻塞其最后已知站点及相邻路段（R4）");

// The parked vehicle is on a short CNC spur while its body still covers the through lane.
var spur = Node("CNC-176", 5, -.053, MapNodeType.Station) with { ResourceIds = ["BAY"] };
var spurDefinition = definition with
{
    Nodes = [.. definition.Nodes, spur],
    Edges = [definition.Edges[0], definition.Edges[1] with { ResourceIds = ["BAY"] }, Edge("SPUR", "B", spur.Id)],
    Resources = [new("BAY", TrafficResourceType.Station, 1)],
};
var parkedBesideLane = Planner(Robot("V2", spur.Id, true) with { Y = spur.Y });
Check(parkedBesideLane.GetVehicleOccupiedEdgeIds(spurDefinition, "A", "V1").Contains("BC"),
    "a parked vehicle on the CNC spur blocks the linked through lane even without a lease");
Check(!parkedBesideLane.GetVehicleOccupiedEdgeIds(spurDefinition, spur.Id, "V2").Contains("BC"),
    "the parked vehicle is excluded from its own departure check");

Console.WriteLine($"Traffic stale occupancy: {checks} checks passed.");

sealed class Vehicles(params AgvRobotSnapshot[] snapshots) : IAgvStateCache
{
    public event Action<AgvRobotStateChanged>? StateChanged;
    public long Version => 1;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => snapshots;
    public bool TryGet(string vehicleId, out AgvRobotSnapshot value)
    {
        var match = snapshots.FirstOrDefault(s => s.VehicleId == vehicleId);
        value = match!;
        return match is not null;
    }
}

sealed class NoFlow : ITrafficFlowMonitor
{
    public TrafficFlowSnapshot GetSnapshot() => new(DateTimeOffset.UtcNow, null, null, [], []);
    public IReadOnlyDictionary<string, double> GetEdgePenalties() => new Dictionary<string, double>();
}
