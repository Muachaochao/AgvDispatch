using System.Reflection;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; Console.WriteLine("PASS " + text); }
MapNode Node(string id, double x, double y, bool enabled = true, string[]? resources = null) => new(id, x, y, MapNodeType.Station, enabled, resources ?? [], "LM-" + id);
MapEdge Edge(string id, string from, string to, double length, bool both = false, bool enabled = true, string[]? resources = null) => new(id, from, to, length, 1, both, enabled, resources ?? []);
var resources = new[] { new TrafficResourceDefinition("R", TrafficResourceType.NarrowAisle) };
var nodes = new[] { Node("PICK", 0, 0), Node("EXIT", 2, 0), Node("WAIT", 4, 0), Node("DUMP", 4, 2) };
var edges = new[] { Edge("PX", "PICK", "EXIT", 2, true, resources: ["R"]), Edge("PW", "PICK", "WAIT", 6), Edge("WD", "WAIT", "DUMP", 2), Edge("DP", "DUMP", "PICK", 6) };
var definition = new MapDefinition("repeat-test", "", "LocalCartesian", "m", nodes, edges, resources);
var graph = definition.ToGraph();
var cycle = graph.FindShortestTransportRoute("PICK", "PICK")!;
Check(cycle.Nodes.SequenceEqual(new[] { "PICK", "EXIT", "PICK" }) && cycle.EdgeIds.SequenceEqual(new[] { "PX", "PX" }) && cycle.Cost == 4, "same-station transport uses real exit and reverse edges, with positive cost");
Check(cycle.ResourceIds.SequenceEqual(new[] { "R" }), "outward and return resources are carried by the cycle");
Check(graph.FindShortestRoute("PICK", "PICK") is { Nodes.Count: 1, Cost: 0 }, "generic shortest-path query retains zero-length semantics");
Check(graph.FindShortestTransportRoute("PICK", "WAIT")!.EdgeIds.SequenceEqual(new[] { "PW" }), "ordinary different-station route is unchanged");
Check(graph.FindShortestTransportRoute("PICK", "PICK", new HashSet<string> { "R" })!.Nodes.SequenceEqual(new[] { "PICK", "WAIT", "DUMP", "PICK" }), "blocked exit resource selects an enabled directed alternative cycle");
Check(graph.FindShortestTransportRoute("PICK", "PICK", edgePenalties: new Dictionary<string, double> { ["PX"] = 20 })!.EdgeIds.SequenceEqual(new[] { "PW", "WD", "DP" }), "cycle optimization includes penalties on outward and return traversals");
Check(graph.FindShortestTransportRoute("PICK", "PICK", new HashSet<string> { "R" }, forbiddenIntermediateNodes: new HashSet<string> { "WAIT", "DUMP" }) is null, "repeat pickup cannot detour through waiting or dumping nodes before its PLC permit");
Check(graph.FindShortestTransportRoute("PICK", "PICK", edgePenalties: new Dictionary<string, double> { ["PX"] = 20 }, forbiddenIntermediateNodes: new HashSet<string> { "WAIT", "DUMP" })!.EdgeIds.SequenceEqual(new[] { "PX", "PX" }), "protected business nodes stay excluded even if their cycle would be cheaper");
var noCycle = definition with { Edges = edges.Select(e => e.Id == "PX" ? e with { Bidirectional = false } : e).Where(e => e.Id != "DP").ToArray() };
Check(noCycle.ToGraph().FindShortestTransportRoute("PICK", "PICK") is null, "one-way exit without return refuses repeat pickup");
Check((definition with { Nodes = nodes.Select(n => n.Id == "PICK" ? n with { Enabled = false } : n).ToArray() }).ToGraph().FindShortestTransportRoute("PICK", "PICK") is null, "disabled pickup refuses cycle");
Check((noCycle with { Edges = noCycle.Edges.Append(Edge("RETURN-DISABLED", "EXIT", "PICK", 2, enabled: false)).ToArray() }).ToGraph().FindShortestTransportRoute("PICK", "PICK") is null, "disabled return edge cannot invent a cycle");
Check((definition with { Nodes = nodes.Select(n => n.Id == "EXIT" ? n with { ResourceIds = ["R"] } : n).ToArray() }).ToGraph().FindShortestTransportRoute("EXIT", "EXIT", new HashSet<string> { "R" }) is null, "blocked source-node resource prevents departure");

var runtime = new ActiveMapRuntime(); runtime.Replace(new("MAP", "V1", definition, DateTimeOffset.Now));
var dispatch = new DispatchOrchestratorOptions { Enabled = true, DumperWaitingSite = "WAIT", DumperDumpingSite = "DUMP", StationPickupSites = new() { ["CNC001"] = "PICK" } };
foreach (var (step, operation) in new[] { ("GoToPickup", "JackLoad"), ("DeliverFullBoxToDumper", "Wait"), ("EnterDumpingPosition", "Wait"), ("ReturnEmptyBox", "JackUnload") }) dispatch.StepActions[step] = new() { Operation = operation };
var flow = new EmptyFlow();
var planner = new ConfiguredRoutePlanner(new MapDefinitionProvider(new(), new MapImportService(), new MapValidationService()), runtime, dispatch, flow);
Check(planner.Plan("LM-PICK", "PICK", new HashSet<string>())!.Nodes.SequenceEqual(new[] { "LM-PICK", "LM-EXIT", "LM-PICK" }), "dispatch planner resolves mixed logical/vendor IDs into actual nonzero vendor path");
Check(planner.Plan("LM-PICK", "PICK", new HashSet<string> { "R" }) is null, "dispatch planner cannot use unpermitted waiting/dumping shortcut when pickup exit is occupied");
var withParking=definition with { Nodes=definition.Nodes.Append(Node("PARK",1,1)).ToArray(),
    Edges=definition.Edges.Concat([Edge("PS","PICK","PARK",.1,true),Edge("SX","PARK","EXIT",.1,true)]).ToArray() };
runtime.Replace(new("MAP","V1",withParking,DateTimeOffset.Now));dispatch.StandbySites=["LM-PARK"];
Check(planner.Plan("LM-PICK","LM-EXIT",new HashSet<string>())!.Nodes.SequenceEqual(["LM-PICK","LM-EXIT"]),"ordinary transport avoids a shorter shortcut through a configured standby bay");
Check(planner.Plan("LM-PARK","LM-EXIT",new HashSet<string>()) is { Nodes.Count:>=2 },"standby protection still permits departure from the actual parked bay");
runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));dispatch.StandbySites=[];
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(Path.GetTempPath(), "agv-pickup-repeat-" + Guid.NewGuid().ToString("N") + ".db") });
var catalog = new TrafficResourceCatalog(resources); await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db); var orchestration = new DispatchOrchestrationRepository(db); var outbox = new AgvCommandOutboxRepository(db, runtime);
var creation = new DispatchTaskCreationOptions(); var parameters = new TaskParameterRuntime(dispatch, creation);
var gate = new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
var vehicle = new Vehicle(); var checker = new LocalChecker(runtime);
var exitArea=new TrafficResourceDefinition("DUMP-AREA",TrafficResourceType.Station,1,[new(3,1),new(5,1),new(5,3),new(3,3)]);
var exitDefinition=definition with {Resources=resources.Append(exitArea).ToArray(),Nodes=nodes.Select(n=>n.Id=="DUMP"?n with{ResourceIds=[exitArea.Id]}:n).ToArray()};
runtime.Replace(new("MAP","V1",exitDefinition,DateTimeOffset.Now));
var handshakeOptions=new HandshakeExecutionOptions{Enabled=true,PlcContractVersion=HandshakeExecutionOptions.Version,DumperExit=new(){ResourceIds=[exitArea.Id]}};
var strictManual=new ManualTaskCreationService(tasks,new Stations(),vehicle,vehicle,runtime,gate,orchestration,creation,dispatch,new(),new(),new PlcConfiguration(),parameters,checker,handshakeOptions);
var strictOptions=await strictManual.GetOptionsAsync(default);
Check(!strictOptions.Vehicles.Single().Available&&strictOptions.Vehicles.Single().Explanation.Contains("唯一"),"manual options block a selected vehicle lacking its unique exit profile before acknowledgment");
var missingProfile=await strictManual.CreateAsync(TrafficUserContext.LocalOperation("isolated-exit-config-test"),new(Guid.NewGuid().ToString("N"),"CNC001","AGV-01",20,"MAP","V1"){ParameterRevision=parameters.Revision},default);
Check(!missingProfile.Accepted&&await db.Queryable<DispatchTaskEntity>().CountAsync()==0,"invalid exit configuration cannot create or acknowledge a task");
var profile=new DumperExitBodyProfile("isolated-loaded-profile","AGV-01",0.5,0.05,true,true);
handshakeOptions.DumperExit.BodyProfiles=[profile with{CalibrationVerified=false}];
Check(!(await strictManual.GetOptionsAsync(default)).Vehicles.Single().Available,"manual options reject unverified vehicle and load envelope");
handshakeOptions.DumperExit.BodyProfiles=[profile];
Check((await strictManual.GetOptionsAsync(default)).Vehicles.Single().Available,"complete per-vehicle exit configuration is eligible before creating a task");
handshakeOptions.DumperExit.BodyProfiles=[profile,profile with{ProfileId="duplicate"}];
Check(!(await strictManual.GetOptionsAsync(default)).Vehicles.Single().Available,"manual options reject ambiguous duplicate profiles");
handshakeOptions.DumperExit.BodyProfiles=[profile];
runtime.Replace(new("MAP","V1",exitDefinition with{Resources=resources.Append(exitArea with{Boundary=null}).ToArray()},DateTimeOffset.Now));
Check(!(await strictManual.GetOptionsAsync(default)).Vehicles.Single().Available,"manual options reject missing region geometry before task persistence");
runtime.Replace(new("MAP","V1",exitDefinition with{Nodes=nodes},DateTimeOffset.Now));
Check(!(await strictManual.GetOptionsAsync(default)).Vehicles.Single().Available,"manual options reject a dump node unrelated to the configured release area");
runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));
var manual = new ManualTaskCreationService(tasks, new Stations(), vehicle, vehicle, runtime, gate, orchestration, creation, dispatch, new(), new(), new PlcConfiguration(), parameters, checker);
var options = await manual.GetOptionsAsync(default);
Check(options.Available && options.Stations.Single().PickupReachableVehicleIds.Contains("AGV-01"), "returning vehicle at pickup is eligible for next legitimate call");
var preview = await manual.PreviewRoutesAsync(new("CNC001", "AGV-01", "MAP", "V1", options.ParameterRevision), default);
Check(preview.Verified && preview.Legs[0].LogicalPath == "PICK → EXIT → PICK" && preview.Legs[0].Message.Contains("实际进出"), "first-leg preview exposes the actual exit/return and final pickup action");
var request = new ManualTaskCreationRequest(Guid.NewGuid().ToString("N"), "CNC001", "AGV-01", 20, "MAP", "V1") { ParameterRevision = options.ParameterRevision };
var created = await manual.CreateAsync(TrafficUserContext.LocalOperation("isolated-repeat-test"), request, default);
Check(created.Created && created.TaskId is not null, "manual creation accepts checked nonzero cycle");
await tasks.MarkAcknowledgedAsync(created.TaskId!, default);
var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromSeconds(120)));
var orchestrator = new DispatchTaskOrchestrator(NullLogger<DispatchTaskOrchestrator>.Instance, dispatch, orchestration, outbox, vehicle, vehicle, runtime, new(), planner, traffic, gate, checker);
await (Task)typeof(DispatchTaskOrchestrator).GetMethod("RunCycleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(orchestrator, new object[] { CancellationToken.None })!;
var command = await db.Queryable<AgvCommandOutboxEntity>().SingleAsync();
var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
Check(payload.Path.SequenceEqual(new[] { "LM-PICK", "LM-EXIT", "LM-PICK" }) && payload.Action?.Operation == "JackLoad", "dispatch persists the same actual cycle and pickup action in Outbox");
Check(command.ExecutionRouteSnapshotJson?.Contains("PX") == true && command.ExecutionRouteSnapshotJson.Contains("LM-EXIT"), "immutable send snapshot records both real traversal edges");
Check(vehicle.CommandCalls == 0 && vehicle.CurrentSite == "LM-PICK", "planning does not send a command, teleport the vehicle or fabricate completion");
var firstSteps = await tasks.GetStepsAsync(created.TaskId!, default);
Check(firstSteps[0].Status != DispatchTaskStepStatus.Completed && firstSteps.Skip(1).All(s => s.Status == DispatchTaskStepStatus.Pending), "first transport and action must still execute before handshake advances");
runtime.Replace(new("MAP", "V1", definition with { Edges = edges.Where(e => e.Id != "PX").ToArray() }, DateTimeOffset.Now));
var blockedPreview = await manual.PreviewRoutesAsync(new("CNC001", "AGV-01", "MAP", "V1", options.ParameterRevision), default);
Check(!blockedPreview.Verified && blockedPreview.Legs[0].Message.Contains("进出闭合路线"), "no-cycle preview explicitly requests a real entry/exit route");
var blocked = await manual.CreateAsync(TrafficUserContext.LocalOperation("isolated-repeat-test"), request with { RequestId = Guid.NewGuid().ToString("N") }, default);
Check(!blocked.Accepted && await db.Queryable<DispatchTaskEntity>().CountAsync() == 1, "no-cycle creation refuses without persisting another same-point task");
Check(planner.Plan("LM-PICK", "PICK", new HashSet<string>()) is null, "dispatcher also refuses when actual graph loses the return route");

await ProtocolChecks.Run(definition, Check);
Console.WriteLine($"PASS: {checks} repeat-pickup routing checks");

sealed class EmptyFlow : ITrafficFlowMonitor
{ public TrafficFlowSnapshot GetSnapshot() => new(DateTimeOffset.Now, null, null, [], []); public IReadOnlyDictionary<string, double> GetEdgePenalties() => new Dictionary<string, double>(); }
sealed class Vehicle : IAgvGateway, IAgvStateCache
{
    public string CurrentSite = "LM-PICK"; public int CommandCalls;
    public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } } public long Version => 1;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => []; public bool TryGet(string id, out AgvRobotSnapshot snapshot)
    { snapshot = new(id, id, "", 0, ConnectionState.Online, SignalQuality.Good, 0, 0, 0, 1, CurrentSite, null, 80, false, false, "Idle", null, null, null, [], [], DateTimeOffset.Now, DateTimeOffset.Now, 0, null, null, 1); return true; }
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([new("AGV-01", ConnectionState.Online, "Idle", 80, CurrentSite, DateTimeOffset.Now)]);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("AGV", "AGV", ConnectionState.Online, DateTimeOffset.Now));
    public Task<string> CreateTransportTaskAsync(string id, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct) { CommandCalls++; throw new Exception("Planning must not dispatch"); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => Task.FromResult<AgvTransportTaskStatus?>(null);
    public Task CancelTaskAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
sealed class Stations : IStationStateService
{
    public event Action<StationStateChanged>? StationStateChanged { add { } remove { } } public long Version => 1;
    public IReadOnlyList<StationStateSnapshot> GetAll() => [];
    public bool TryGet(string id, out StationStateSnapshot snapshot) { snapshot = new(id, default, true, null, true, true, false, true, SignalQuality.Good, null, 0, DateTimeOffset.Now, DateTimeOffset.Now, 1); return true; }
}
sealed class PlcConfiguration : IPlcConfigurationInspector
{ public string? GetSignalConfigurationError(string stationId, string variableName, bool write) => null; }
sealed class LocalChecker(ActiveMapRuntime runtime) : IRobokitTaskMapPreflight
{
    public Task<RobokitTaskMapCheck> CheckAsync(string vehicleId, string currentSite, string pickup, string waiting, string dumping, string mapId, string versionId, CancellationToken ct)
    {
        var map = runtime.Current!; var checks = new List<RobokitTaskRouteCheck>();
        foreach (var (sequence, from, to) in new[] { (1, currentSite, pickup), (3, pickup, waiting), (5, waiting, dumping), (7, dumping, pickup) })
        {
            var a = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, from); var b = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, to);
            var route = sequence == 1 ? map.Graph.FindShortestTransportRoute(a, b, forbiddenIntermediateNodes: new[] { waiting, dumping }.Select(site => MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, site)).ToHashSet(StringComparer.OrdinalIgnoreCase)) : map.Graph.FindShortestRoute(a, b);
            if (route is null) return Task.FromResult(new RobokitTaskMapCheck(false, "No actual route"));
            var path = MapRouteSiteMapper.ToAgvSitePlan(map.Definition, route).Nodes;
            checks.AddRange(path.Zip(path.Skip(1)).Select(pair => new RobokitTaskRouteCheck(sequence, pair.First, pair.Second, true, "isolated route fixture")));
        }
        return Task.FromResult(new RobokitTaskMapCheck(true, "isolated route fixture") { RouteChecks = checks });
    }
}
