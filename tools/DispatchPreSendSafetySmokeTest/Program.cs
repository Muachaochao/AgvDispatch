using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(Path.GetTempPath(), "agv-presend-" + Guid.NewGuid().ToString("N"), "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db); var orchestration = new DispatchOrchestrationRepository(db);
var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromSeconds(30)));
var map = new ActiveMapRuntime();
map.Replace(new("M", "V1", new("fixture", "synthetic", "Cartesian", "m",
    [new("BUS-A", 0, 0, MapNodeType.Station, true, [], "A"), new("BUS-B", 10, 0, MapNodeType.Station, true, [], "B")],
    [new("AB", "BUS-A", "BUS-B", 10, 1, true, true, ["R"])], [new("R", TrafficResourceType.NarrowAisle)]), DateTimeOffset.UtcNow));
var outbox = new AgvCommandOutboxRepository(db, map);
var cache = new VehicleCache(); var signalCache = new Signals(); var gate = new Gate(); var monitor = new Monitor();
var dispatch = new DispatchOrchestratorOptions { Enabled = true, DumperWaitingSite = "B", DumperDumpingSite = "B" };
var handshake = new HandshakeExecutionOptions();
var gateway = DispatchProxy.Create<IAgvGateway, Gateway>(); ((Gateway)gateway).Cache = cache;
var guard = new DispatchPreSendSafetyService(gate, map, cache, gateway, new MapOptions(), dispatch,
    orchestration, tasks, traffic, new() { DumperStationId = "DUMP" }, signalCache, handshake, monitor: monitor);
foreach (var item in new[] { ("ST", "Faulted", false), ("ST", "CommunicationOnline", true), ("ST", "BoxPresent", true),
    ("ST", "FullBoxRequestAcknowledged", true), ("DUMP", "Faulted", false) }) signalCache.Put(item.Item1, item.Item2, item.Item3);
var created = await tasks.TryCreateAsync("ST", "DUMP", 50, default);
await tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
var step = (await orchestration.GetReadyStepsAsync(100, default)).Single(s => s.TaskId == created.Task.TaskId);
var currentMap = map.Current;
await traffic.RecordRouteAsync(new(step.StepId, step.TaskId, "V", "A", "B", ["AB"], ["R"], false, "fixture") { MapId = "M", MapVersionId = "V1" }, default);
await traffic.TryAcquireAsync(step.StepId, step.TaskId, "V", ["R"], default);
Task<string?> Prepare() => guard.CheckPreparationAsync(step, "V", ["A", "B"], currentMap, default);
Check(await Prepare() == null,
    "shared preparation compares the planned AGV site ids used by the traffic request instead of remapping them to logical BUS-* ids");
var goodVehicle = cache.Row;
cache.Row = goodVehicle with { LastSuccessfulPollAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
Check(await Prepare() != null, "stale robot observation blocks preparation"); cache.Row = goodVehicle;
cache.Row = goodVehicle with { EmergencyStopped = true };
Check(await Prepare() != null, "emergency stop blocks preparation"); cache.Row = goodVehicle;
cache.Row = goodVehicle with { Blocked = true };
Check(await Prepare() != null, "blocked robot cannot be prepared"); cache.Row = goodVehicle;
cache.Row = goodVehicle with { Tasks = [new("OTHER", 2, null)] };
Check(await Prepare() != null, "unfinished robot task cannot be stolen"); cache.Row = goodVehicle;
cache.Row = goodVehicle with { BatteryPercent = 5 };
Check(await Prepare() != null, "low battery blocks new business"); cache.Row = goodVehicle;
cache.Row = goodVehicle with { BatteryPercent = 101 };
Check(await Prepare() != null, "invalid battery percentage cannot pass new-business safety"); cache.Row = goodVehicle;
cache.Row = goodVehicle with { CurrentStation = "B" };
Check(await Prepare() != null, "changed robot start blocks the prepared route"); cache.Row = goodVehicle;
signalCache.Put("ST", "FullBoxRequestAcknowledged", false);
Check(await Prepare() != null, "actual PLC acknowledgement is rechecked before preparation"); signalCache.Put("ST", "FullBoxRequestAcknowledged", true);
signalCache.Put("ST", "BoxPresent", true, DateTimeOffset.UtcNow.AddMinutes(-1));
Check(await Prepare() != null, "old good-quality PLC value does not count as fresh permission"); signalCache.Put("ST", "BoxPresent", true);
signalCache.Put("ST", "CommunicationOnline", false);
Check(await Prepare() != null, "actual station communication bit is enforced"); signalCache.Put("ST", "CommunicationOnline", true);
handshake.Enabled = true; handshake.PlcContractVersion = HandshakeExecutionOptions.Version;
Check(await Prepare() != null, "28-stage mode cannot silently fall back without its real executor and binding store"); handshake.Enabled = false;
gate.Mode = MapExecutionMode.Draining;
Check(await Prepare() != null, "draining map cannot start new business"); gate.Mode = MapExecutionMode.Running;
var previousMap = map.Current!; map.Replace(new("M", "V1", previousMap.Definition, DateTimeOffset.UtcNow));
Check(await Prepare() != null, "same-version runtime reactivation invalidates a prepared route"); map.Replace(previousMap);
Check(await outbox.TryPrepareTransportStepAsync(step.TaskId, step.StepId, "V", ["A", "B"], null, default), "first prepared command is persisted atomically");
var pending = (await outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddSeconds(1), 10, default)).Single();
Check(await guard.CheckPendingCommandAsync(pending, default) == null, "first physical attempt passes the shared final preclaim check");
gate.Mode = MapExecutionMode.Draining;
Check(await guard.CheckPendingCommandAsync(pending, default) == null, "draining permits an already prepared existing command"); gate.Mode = MapExecutionMode.Running;
gate.Mode = MapExecutionMode.Held;
Check(await guard.CheckPendingCommandAsync(pending, default) != null, "startup recovery hold blocks final claim"); gate.Mode = MapExecutionMode.Running;
signalCache.Put("ST", "BoxPresent", false);
Check(await guard.CheckPendingCommandAsync(pending, default) != null, "PLC permission change between preparation and send blocks first attempt"); signalCache.Put("ST", "BoxPresent", true);
Check((await outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddSeconds(1), 10, default)).Single().AttemptCount == 0,
    "failed preclaim checks leave attempt count untouched");
var second = await tasks.TryCreateAsync("OTHER-ST", "DUMP", 50, default); await tasks.MarkAcknowledgedAsync(second.Task.TaskId, default);
var secondStep = (await tasks.GetStepsAsync(second.Task.TaskId, default)).First();
Check(!await outbox.TryPrepareTransportStepAsync(second.Task.TaskId, secondStep.StepId, "V", ["A", "B"], null, default),
    "repository transaction rejects another task preparing the same occupied car");
Check((await orchestration.GetDispatchCandidatesAsync(100, default)).Any(t => t.TaskId == second.Task.TaskId) &&
    (await tasks.GetStepsAsync(second.Task.TaskId, default)).First().Status == DispatchTaskStepStatus.Ready,
    "rejected duplicate assignment leaves waiting task and ready step unchanged");
var stamp = DateTimeOffset.UtcNow;
Check(await outbox.TryClaimAsync(pending.CommandId, stamp, default) != null, "first atomic claim succeeds");
Check(await outbox.TryClaimAsync(pending.CommandId, stamp, default) == null,
    "second claim with identical timestamp cannot return another caller's Sending row");
await outbox.ScheduleRetryAsync(pending.CommandId, "uncertain fixture", DateTimeOffset.UtcNow, 5, default);
var retry = (await outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddSeconds(1), 10, default)).Single();
cache.Row = goodVehicle with { CurrentStation = "B", Tasks = [new(step.StepId, 2, null)] };
signalCache.Put("ST", "BoxPresent", false);
Check(await guard.CheckPendingCommandAsync(retry, default) == null,
    "uncertain prior attempt can reconcile original StepId after movement and consumed PLC conditions");
await db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Faulted)
    .Where(t => t.TaskId == step.TaskId).ExecuteCommandAsync();
Check(!await outbox.TryPrepareTransportStepAsync(second.Task.TaskId, secondStep.StepId, "V", ["A", "B"], null, default),
    "faulted original task retains its physical lease vehicle against new preparation");
Check(monitor.States.Contains("Ready") && monitor.States.Contains("Blocked"), "fifth layer reports actual passing and blocking work");
// Exercise the production wiring as well as the direct contract above.
await traffic.ReleaseTaskAsync(step.TaskId, default);
cache.Row = goodVehicle with { LastSuccessfulPollAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
foreach (var item in new[] { ("Faulted", false), ("CommunicationOnline", true), ("BoxPresent", true), ("FullBoxRequestAcknowledged", true) })
    signalCache.Put("PIPELINE-ST", item.Item1, item.Item2);
dispatch.StationPickupSites["PIPELINE-ST"] = "B";
var pipelineTask = await tasks.TryCreateAsync("PIPELINE-ST", "DUMP", 50, default);
await tasks.MarkAcknowledgedAsync(pipelineTask.Task.TaskId, default);
var policy = new DispatchAssignmentOptions { Mode = "Global" };
var optimization = new DispatchOptimizationService(Path.Combine(Path.GetTempPath(),
    "unused-safety-options-" + Guid.NewGuid().ToString("N") + ".json"), policy,
    Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchOptimizationService>.Instance);
var orchestrator = new DispatchTaskOrchestrator(Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchTaskOrchestrator>.Instance,
    dispatch, orchestration, outbox, gateway, cache, map, new MapOptions(), new Planner(), traffic, gate,
    optimization: optimization, preSendSafety: guard);
var optimizationCycle = typeof(DispatchTaskOrchestrator).GetMethod("RunOptimizationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
signalCache.Put("PIPELINE-ST", "FullBoxRequestAcknowledged", false);
await (Task)optimizationCycle.Invoke(orchestrator, [policy, 1, Array.Empty<DispatchTaskWorkItem>(), CancellationToken.None])!;
Check(!await db.Queryable<AgvCommandOutboxEntity>().AnyAsync(c => c.TaskId == pipelineTask.Task.TaskId) &&
    !await db.Queryable<TrafficResourceLeaseEntity>().AnyAsync(l => l.TaskId == pipelineTask.Task.TaskId),
    "actual global orchestrator calls shared PLC safety and rolls back uncommitted rights before command preparation");
signalCache.Put("PIPELINE-ST", "FullBoxRequestAcknowledged", true);
await (Task)optimizationCycle.Invoke(orchestrator, [policy, 1, Array.Empty<DispatchTaskWorkItem>(), CancellationToken.None])!;
Check(await db.Queryable<AgvCommandOutboxEntity>().CountAsync(c => c.TaskId == pipelineTask.Task.TaskId) == 1,
    "actual global pipeline prepares exactly one original command when current PLC permission becomes valid");
signalCache.Put("PIPELINE-ST", "BoxPresent", false);
var dispatcher = new AgvCommandOutboxDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<AgvCommandOutboxDispatcher>.Instance,
    new() { Enabled = true }, outbox, gateway, gate, trafficResources: traffic, activeMap: map, preSendSafety: guard);
var dispatchCycle = typeof(AgvCommandOutboxDispatcher).GetMethod("DispatchDueCommandsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
await (Task)dispatchCycle.Invoke(dispatcher, [CancellationToken.None])!;
var blockedCommand = await db.Queryable<AgvCommandOutboxEntity>().Where(c => c.TaskId == pipelineTask.Task.TaskId).SingleAsync();
Check(blockedCommand.AttemptCount == 0 && blockedCommand.Status == (int)AgvCommandStatus.Pending,
    "actual outbox shared preclaim safety catches PLC change after global preparation without consuming a send attempt");
Console.WriteLine($"PASS {checks} common safety and atomic deduplication checks; isolated SQLite, synthetic map and fake gateway only.");

sealed class Signals : IPlcSignalCache
{
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } }
    private readonly Dictionary<(string, string), PlcSignalSnapshot> _rows = [];
    public long Version => 1;
    public void Put(string station, string variable, bool value, DateTimeOffset? time = null) =>
        _rows[(station, variable)] = new(station, variable, variable, "fixture", "BOOL", "Read", "Read", true,
            value, SignalQuality.Good, time ?? DateTimeOffset.UtcNow, time ?? DateTimeOffset.UtcNow, 1);
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => _rows.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string id) => _rows.Values.Where(r => r.StationId == id).ToArray();
    public bool TryGet(string station, string variable, out PlcSignalSnapshot row) => _rows.TryGetValue((station, variable), out row!);
}
sealed class VehicleCache : IAgvStateCache
{
    public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } }
    public long Version => 1;
    public AgvRobotSnapshot Row = new("V", "fixture", "127.0.0.1", 1, ConnectionState.Online, SignalQuality.Good,
        0, 0, 0, 1, "A", "A", 80, false, false, "Idle", null, null, 0, [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 1, null, 1);
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => [Row];
    public bool TryGet(string id, out AgvRobotSnapshot row) { row = Row; return id == Row.VehicleId; }
}
public class Gateway : DispatchProxy
{
    internal VehicleCache Cache = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetConnectionStatusAsync" => Task.FromResult(new DeviceConnectionStatus("fleet", "fixture", ConnectionState.Online, DateTimeOffset.UtcNow)),
        "GetVehiclesAsync" => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([new("V", Cache.Row.Connection,
            Cache.Row.Tasks.Any(t => t.Status is 1 or 2 or 3) ? "Busy" : "Idle", Cache.Row.BatteryPercent ?? 0,
            Cache.Row.CurrentStation, Cache.Row.UpdatedAt)]),
        _ => throw new NotSupportedException("This safety test never sends device commands: " + method?.Name)
    };
}
sealed class Planner : IRoutePlanner
{
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) =>
        source == "A" && destination == "B" && !unavailable.Contains("R") ? new(["A", "B"], ["AB"], ["R"], 10) : null;
}
sealed class Gate : IMapExecutionGate
{
    public MapExecutionMode Mode { get; set; } = MapExecutionMode.Running;
    public bool CanStartNewWork => Mode == MapExecutionMode.Running;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation op, CancellationToken ct) => throw new Exception("Nested map operation lease is forbidden in the helper");
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct) => throw new NotSupportedException();
    public void SetMode(MapExecutionMode mode) => Mode = mode;
}
sealed class Monitor : IDispatchPipelineMonitor
{
    public List<string> States = [];
    public void ReportLayer(string key, string state, string summary, int? itemCount = null) { if (key == "safety") States.Add(state); }
}
