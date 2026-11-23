using System.Reflection;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

// D3: a second-or-later window command that the vehicle accepted, followed by a lost
// response or a failed acknowledgement journal, must reconcile by its StepId and reach Sent.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var root = Path.Combine(Path.GetTempPath(), "agv-continuation-retry-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle), new("R2", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db); var orchestration = new DispatchOrchestrationRepository(db);
var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromSeconds(30)));
var map = new ActiveMapRuntime();
map.Replace(new("M", "V1", new("fixture", "synthetic", "Cartesian", "m",
    [new("BUS-A", 0, 0, MapNodeType.Station, true, [], "A"), new("BUS-B", 10, 0, MapNodeType.Station, true, [], "B"),
     new("BUS-C", 0, 10, MapNodeType.Station, true, [], "C"), new("BUS-D", 10, 10, MapNodeType.Station, true, [], "D")],
    [new("AB", "BUS-A", "BUS-B", 10, 1, true, true, ["R"]), new("CD", "BUS-C", "BUS-D", 10, 1, true, true, ["R2"])],
    [new("R", TrafficResourceType.NarrowAisle), new("R2", TrafficResourceType.NarrowAisle)]), DateTimeOffset.UtcNow));
var outbox = new AgvCommandOutboxRepository(db, map);
var cache = new VehicleCache(); var signals = new Signals(); var gate = new Gate(); var monitor = new Monitor();
var dispatch = new DispatchOrchestratorOptions { Enabled = true, DumperWaitingSite = "B", DumperDumpingSite = "B" };
var gateway = DispatchProxy.Create<IAgvGateway, SendGateway>(); var fake = (SendGateway)gateway; fake.Cache = cache;
var guard = new DispatchPreSendSafetyService(gate, map, cache, gateway, new MapOptions(), dispatch,
    orchestration, tasks, traffic, new() { DumperStationId = "DUMP" }, signals, new HandshakeExecutionOptions(), monitor: monitor);
foreach (var item in new[] { ("ST", "Faulted", false), ("ST", "CommunicationOnline", true), ("ST", "BoxPresent", true),
    ("ST", "FullBoxRequestAcknowledged", true), ("DUMP", "Faulted", false), ("ST2", "Faulted", false), ("ST2", "CommunicationOnline", true),
    ("ST2", "BoxPresent", true), ("ST2", "FullBoxRequestAcknowledged", true) }) signals.Put(item.Item1, item.Item2, item.Item3);
var journal = new Journal();
var dispatcher = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance, new AgvCommandOutboxOptions(),
    outbox, gateway, gate, journal);
var send = typeof(AgvCommandOutboxDispatcher).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
Task Send(AgvCommandOutboxItem command) => (Task)send.Invoke(dispatcher, [command, CancellationToken.None])!;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// Creates a task whose first leg has been sent (window 0 accepted), then queues window 1 with its vendor id.
// Window 0 runs siteA->siteB (already accepted); window 1 is the continuation siteB->siteA on the same edge.
async Task<(string TaskId, string StepId, AgvCommandOutboxItem Command)> PrepareContinuationAsync(string station, string vehicle, string previousVendor, string resource, string edge, string siteA, string siteB)
{
    var busA = "BUS-" + siteA; var busB = "BUS-" + siteB;
    var created = await tasks.TryCreateAsync(station, "DUMP", 50, default);
    await tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
    var step = (await tasks.GetStepsAsync(created.Task.TaskId, default)).First();
    await traffic.RecordRouteAsync(new(step.StepId, step.TaskId, vehicle, siteB, siteA, [edge], [resource], false, "fixture") { MapId = "M", MapVersionId = "V1" }, default);
    await db.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity
    {
        Status = (int)DispatchTaskStepStatus.Executing, VendorTaskId = previousVendor, VehicleId = vehicle,
        SourceSite = busA, DestinationSite = busB
    }).Where(s => s.StepId == step.StepId).ExecuteCommandAsync();
    await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity
    {
        Stage = (int)DispatchTaskStage.GoingToPickup, VendorTaskId = previousVendor, VehicleId = vehicle
    }).Where(t => t.TaskId == created.Task.TaskId).ExecuteCommandAsync();
    await traffic.TryAcquireAsync(step.StepId, step.TaskId, vehicle, [resource], default);
    var windows = new AgvTransportRouteWindow[]
    {
        new(0, siteA, siteB, [siteA, siteB], [edge], [resource], false),
        new(1, siteB, siteA, [siteB, siteA], [edge], [resource], true)
    };
    Check(await outbox.TryPrepareTransportWindowAsync(created.Task.TaskId, step.StepId, vehicle, busA, busB,
        windows[1], windows, previousVendor, null, default), "a later window command is persisted only for the owning vehicle");
    var command = (await outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50, default))
        .Single(c => c.TaskId == created.Task.TaskId);
    return (created.Task.TaskId, step.StepId, command);
}

Task<AgvCommandOutboxEntity> RowAsync(string commandId) => db.Queryable<AgvCommandOutboxEntity>()
    .Where(c => c.CommandId == commandId).FirstAsync();

// Scenario 1: the vehicle accepts window 2 but the response is lost, so the command is retried.
var goodVehicle = cache.Row;
var first = await PrepareContinuationAsync("ST", "V", "V-WINDOW1", "R", "AB", "A", "B");
// Idle at the start of the continuation window, B (X=10, Y=0 on the fixture map).
cache.Row = goodVehicle with { CurrentStation = "B", X = 10, Y = 0, LastSuccessfulPollAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
Check(await guard.CheckPendingCommandAsync(first.Command, default) == null,
    "the continuation command passes the ordinary pre-claim checks while the car is idle at the window start");
var claimedFirst = await outbox.TryClaimAsync(first.Command.CommandId, DateTimeOffset.UtcNow, default);
Check(claimedFirst is not null, "the continuation command is claimed once");
fake.LoseResponseOnce = true;
await Send(claimedFirst!);
Check((await RowAsync(first.Command.CommandId)).Status == (int)AgvCommandStatus.RetryScheduled,
    "a lost response after acceptance is persisted for retry, never treated as sent");
// The car has already started the accepted window, so it is not idle at the window start.
cache.Row = goodVehicle with
{
    VehicleId = "V", CurrentStation = "X", VelocityX = 0.3, LastSuccessfulPollAt = DateTimeOffset.UtcNow,
    UpdatedAt = DateTimeOffset.UtcNow, Tasks = [new(first.StepId, 2, null)]
};
// The retry is scheduled after the configured backoff; query beyond it.
var retry = (await outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddMinutes(5), 50, default)).Single(c => c.CommandId == first.Command.CommandId);
Check(await guard.CheckPendingCommandAsync(retry, default) == null,
    "a later-window retry reconciles by StepId instead of requiring an idle car at the window start");
var claimedRetry = await outbox.TryClaimAsync(retry.CommandId, DateTimeOffset.UtcNow.AddMinutes(5), default);
Check(claimedRetry is not null, "the retry claims the same command");
await Send(claimedRetry!);
var afterRetry = await RowAsync(first.Command.CommandId);
Check(afterRetry.Status == (int)AgvCommandStatus.Sent && afterRetry.VendorCommandId == fake.Accepted.Values.Single(),
    "the retry reuses the vendor task accepted earlier and marks the command Sent");
Check((await db.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == first.TaskId).FirstAsync()).VendorTaskId == afterRetry.VendorCommandId,
    "the task records the later-window vendor task after the retry");

// Scenario 2: the vehicle accepts window 2 and the acknowledgement journal write fails.
var second = await PrepareContinuationAsync("ST2", "W", "W-WINDOW1", "R2", "CD", "C", "D");
cache.Other = null;
var claimedSecond = await outbox.TryClaimAsync(second.Command.CommandId, DateTimeOffset.UtcNow, default);
Check(claimedSecond is not null, "the second continuation command is claimed once");
cache.Other = goodVehicle with { VehicleId = "W", CurrentStation = "D", X = 10, Y = 10, Tasks = [new(second.StepId, 2, null)], VelocityX = 0.3, LastSuccessfulPollAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
journal.FailAcknowledgement = true;
await Send(claimedSecond!);
journal.FailAcknowledgement = false;
var secondRow = await RowAsync(second.Command.CommandId);
Check(secondRow.Status == (int)AgvCommandStatus.Sent && secondRow.VendorCommandId is not null,
    "an acknowledgement journal failure after acceptance does not block MarkSent");
Check(journal.Kinds.Contains("AgvSendAcknowledged") && journal.Failed == 1, "the journal failure was observed and absorbed");

Console.WriteLine($"PASS {checks} continuation retry checks; isolated SQLite and fake gateway only.");

sealed class Journal : IExecutionEvidenceJournal
{
    public bool FailAcknowledgement;
    public int Failed;
    public List<string> Kinds = [];
    public IDisposable BeginScope(string taskId, string stepId) => new Scope();
    public Task RecordAsync(string taskId, string stepId, string kind, object evidence, CancellationToken ct)
    {
        Kinds.Add(kind);
        if (FailAcknowledgement && kind == "AgvSendAcknowledged") { Failed++; throw new IOException("journal busy"); }
        return Task.CompletedTask;
    }
    public Task RecordCurrentAsync(string kind, object evidence, CancellationToken ct) => Task.CompletedTask;
    private sealed class Scope : IDisposable { public void Dispose() { } }
}
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
    public AgvRobotSnapshot? Other;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => Other is null ? [Row] : [Row, Other];
    public bool TryGet(string id, out AgvRobotSnapshot row)
    {
        if (id == Row.VehicleId) { row = Row; return true; }
        if (Other is not null && id == Other.VehicleId) { row = Other; return true; }
        row = null!; return false;
    }
}
public class SendGateway : DispatchProxy
{
    internal VehicleCache Cache = null!;
    public bool LoseResponseOnce;
    public readonly Dictionary<string, string> Accepted = new(StringComparer.Ordinal);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetConnectionStatusAsync" => Task.FromResult(new DeviceConnectionStatus("fleet", "fixture", ConnectionState.Online, DateTimeOffset.UtcNow)),
        "GetVehiclesAsync" => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>(Cache.GetAll().Select(row =>
            new AgvVehicleStatus(row.VehicleId, row.Connection, row.Tasks.Any(t => t.Status is 1 or 2 or 3) ? "Busy" : "Idle",
                row.BatteryPercent ?? 0, row.CurrentStation, row.UpdatedAt)).ToArray()),
        "CreateTransportTaskAsync" => Create((string)args![0]!),
        _ => throw new NotSupportedException("This continuation test sends only through the fake transport: " + method?.Name)
    };
    private Task<string> Create(string businessTaskId)
    {
        // The vehicle accepts the first send of each business id; a repeat returns the existing vendor task.
        if (!Accepted.TryGetValue(businessTaskId, out var vendor))
            Accepted[businessTaskId] = vendor = businessTaskId + "-VENDOR";
        if (LoseResponseOnce)
        {
            LoseResponseOnce = false;
            return Task.FromException<string>(new TimeoutException("response lost after the vehicle accepted the command"));
        }
        return Task.FromResult(vendor);
    }
}
sealed class Gate : IMapExecutionGate
{
    private int _depth;
    public MapExecutionMode Mode { get; set; } = MapExecutionMode.Running;
    public bool CanStartNewWork => Mode == MapExecutionMode.Running;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation op, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _depth) != 1) { Interlocked.Decrement(ref _depth); throw new InvalidOperationException("Nested map operation lease is forbidden in the helper"); }
        return ValueTask.FromResult<IDisposable?>(new Lease(this));
    }
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct) => throw new NotSupportedException();
    public void SetMode(MapExecutionMode mode) => Mode = mode;
    private sealed class Lease(Gate owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._depth); }
    }
}
sealed class Monitor : IDispatchPipelineMonitor
{
    public void ReportLayer(string key, string state, string summary, int? itemCount = null) { }
}
