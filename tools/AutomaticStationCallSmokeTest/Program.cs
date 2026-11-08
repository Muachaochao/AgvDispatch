using System.Collections.Concurrent;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

// Real formal PLC ingress and SQLite persistence; no vehicle, field endpoint or
// scene-script task API participates in this regression.
var root = Path.Combine(Path.GetTempPath(), "agv-auto-station-call-" + Guid.NewGuid().ToString("N"));
using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "private.db") });
await new DatabaseInitializer(database, new TrafficResourceCatalog([])).InitializeAsync(default);
var repository = new RecordingRepository(new DispatchTaskRepository(database));
var stations = new StationInputs("CNC-001", "CNC-002", "CNC-009");
var plc = new LoopbackPlc { DenySecondStation = true };
var gate = new MapExecutionGate();
gate.SetMode(MapExecutionMode.Running);
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
PlcSignalSnapshot Signal(string name, bool value) =>
    new("CNC-005", name, name, "DB1.DBX0.0", "Bool",
        "PlcToHost", "Read", true, value, SignalQuality.Good,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
var idleSignals = new[]
{
    Signal("FullBoxRequested", false),
    Signal("BoxPresent", false),
    Signal("Faulted", false),
    Signal("CommunicationOnline", true),
    Signal("FullBoxRequestAcknowledged", false),
    Signal("FullBoxRemoved", false),
    Signal("EmptyBoxTaskAcknowledged", false),
    Signal("EmptyBoxPlaced", false)
};
Check(StationStateEvaluator.Evaluate("CNC-005", idleSignals, []).State == StationOperationalState.Idle,
    "空工位、无任务且握手已清理时保持正常待料，不产生信号冲突");
var staleHandshake = idleSignals.Select(signal =>
    signal.VariableName == "EmptyBoxTaskAcknowledged" ? signal with { Value = true } : signal).ToArray();
Check(StationStateEvaluator.Evaluate("CNC-005", staleHandshake, []).State == StationOperationalState.SignalConflict,
    "空工位仍有历史握手输出时继续报告信号冲突");
async Task Until(Func<bool> condition, string label)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException(label);
        await Task.Delay(20);
    }
}
TaskCreationCoordinator Worker(DispatchTaskCreationOptions options) => new(
    NullLogger<TaskCreationCoordinator>.Instance, options, stations, repository, plc, gate,
    new TaskParameterRuntime(new(), options));

var options = new DispatchTaskCreationOptions
{
    Enabled = true, AutomaticCreationEnabled = true, DefaultPriority = 17,
    AcknowledgementRetryIntervalMs = 500,
    StationPriorities = new(StringComparer.OrdinalIgnoreCase) { ["CNC-001"] = 0, ["CNC-002"] = 100 }
};
Check(options.PriorityFor("cnc-002") == 100 && options.PriorityFor("CNC-009") == 17,
    "station priority uses configured value and falls back to default for other configured stations");
using (var worker = Worker(options))
{
    await worker.StartAsync(default);
    try
    {
        await plc.FirstDenied.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Until(() => database.Queryable<DispatchTaskEntity>().Count() == 3, "automatic ingress did not persist all three calls");
        var original = database.Queryable<DispatchTaskEntity>().ToList().ToDictionary(task => task.StationId);
        Check(original["CNC-001"].Priority == 0 && original["CNC-002"].Priority == 100 && original["CNC-009"].Priority == 17,
            "formal automatic PLC ingress persists three station priorities including both allowed boundary values");
        Check(original.Values.All(task => task.VehicleId is null && task.RequestedVehicleId is null && task.CreateRequestId is null),
            "PLC-generated tasks leave vehicle selection to formal dispatch without manual creation identities");
        Check(original["CNC-002"].Stage == (int)DispatchTaskStage.Created &&
              (await repository.GetPendingAcknowledgementsAsync(default)).Any(task => task.TaskId == original["CNC-002"].TaskId),
            "failed PLC response leaves the original unacknowledged task durable for retry");
        Check(database.Queryable<DispatchTaskStepEntity>().Count() == 27,
            "each of three persisted calls has exactly one complete set of nine formal workflow steps");

        plc.DenySecondStation = false;
        await Until(() => database.Queryable<DispatchTaskEntity>().Count(task => task.Stage == (int)DispatchTaskStage.WaitingForVehicle) == 3,
            "formal PLC acknowledgement retries did not ready all waiting calls");
        var afterRetry = database.Queryable<DispatchTaskEntity>().Single(task => task.TaskId == original["CNC-002"].TaskId);
        Check(plc.Attempts.GetValueOrDefault("CNC-002") >= 2 && afterRetry.CreatedAt == original["CNC-002"].CreatedAt && afterRetry.Priority == 100,
            "PLC acknowledgement retry retains the exact original task, call time and priority");

        options.StationPriorities["CNC-002"] = 4;
        var priorAttempts = repository.Attempts.GetValueOrDefault("CNC-002");
        for (var index = 0; index < 8; index++) stations.RepeatCall("CNC-002");
        await Until(() => repository.Attempts.GetValueOrDefault("CNC-002") > priorAttempts, "repeat call was not processed");
        var afterDuplicate = database.Queryable<DispatchTaskEntity>().Single(task => task.StationId == "CNC-002");
        Check(afterDuplicate.TaskId == original["CNC-002"].TaskId && afterDuplicate.CreatedAt == original["CNC-002"].CreatedAt && afterDuplicate.Priority == 100,
            "repeated station calls reuse the original task and do not rewrite an existing priority or waiting age");
        Check(database.Queryable<DispatchTaskEntity>().Count() == 3 && database.Queryable<DispatchTaskStepEntity>().Count() == 27,
            "repeated automatic calls cannot create duplicate task or workflow rows");
        Check(plc.Attempts.GetValueOrDefault("CNC-001") == 1 && plc.Attempts.GetValueOrDefault("CNC-009") == 1,
            "already acknowledged stations are not repeatedly acknowledged while waiting for vehicles");
    }
    finally { await worker.StopAsync(default); }
}

foreach (var invalid in new DispatchTaskCreationOptions[]
{
    new() { DefaultPriority = -1 }, new() { DefaultPriority = 101 },
    new() { StationPriorities = new() { ["CNC-001"] = -1 } },
    new() { StationPriorities = new() { ["CNC-001"] = 101 } },
    new() { StationPriorities = new() { [" "] = 50 } }
})
{
    using var worker = Worker(invalid);
    Exception? failure = null;
    try
    {
        await worker.StartAsync(default);
        if (worker.ExecuteTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    catch (Exception error) { failure = error; }
    finally
    {
        try { await worker.StopAsync(default); } catch (InvalidOperationException) { }
    }
    Check(failure is InvalidOperationException && failure.Message.Contains("0至100", StringComparison.Ordinal),
        "invalid station/default priority is rejected at formal worker startup with a clear range explanation");
}
Check(database.Queryable<DispatchTaskEntity>().Count() == 3,
    "rejected worker configurations have no persisted task side effects");
Console.WriteLine($"PASS: {checks} automatic station-call/priority checks. Private database: {root}");

sealed class StationInputs(params string[] ids) : IStationStateService
{
    readonly Dictionary<string, StationStateSnapshot> states = ids.ToDictionary(id => id, id => new StationStateSnapshot(
        id, default, true, null, true, true, false, true, SignalQuality.Good, null, 0,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
    public event Action<StationStateChanged>? StationStateChanged;
    public long Version => 1;
    public IReadOnlyList<StationStateSnapshot> GetAll() => states.Values.ToArray();
    public bool TryGet(string id, out StationStateSnapshot snapshot) => states.TryGetValue(id, out snapshot!);
    public void RepeatCall(string id) => StationStateChanged?.Invoke(new(id, null, states[id], DateTimeOffset.UtcNow));
}

sealed class LoopbackPlc : IPlcGateway
{
    public volatile bool DenySecondStation;
    public readonly ConcurrentDictionary<string, int> Attempts = new();
    public readonly TaskCompletionSource FirstDenied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WriteSignalAsync(string station, string signal, bool value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (signal != "FullBoxRequestAcknowledged" || !value) throw new InvalidOperationException("Unexpected PLC operation");
        Attempts.AddOrUpdate(station, 1, (_, count) => count + 1);
        if (station == "CNC-002" && DenySecondStation)
        {
            FirstDenied.TrySetResult();
            throw new IOException("Isolated PLC acknowledgement deliberately unavailable");
        }
        return Task.CompletedTask;
    }
    public Task<bool?> ReadSignalAsync(string station, string signal, CancellationToken token) => Task.FromResult<bool?>(null);
    public Task<StationSignal?> ReadStationAsync(string station, CancellationToken token) => Task.FromResult<StationSignal?>(null);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken token) => Task.FromResult(
        new DeviceConnectionStatus("ISOLATED", "Private test PLC", ConnectionState.Online, DateTimeOffset.UtcNow));
}

sealed class RecordingRepository(IDispatchTaskRepository inner) : IDispatchTaskRepository
{
    public readonly ConcurrentDictionary<string, int> Attempts = new();
    public async Task<TaskCreationResult> TryCreateAsync(string station, string dumper, int priority, CancellationToken token)
    {
        var result = await inner.TryCreateAsync(station, dumper, priority, token);
        Attempts.AddOrUpdate(station, 1, (_, count) => count + 1);
        return result;
    }
    public Task<IReadOnlyList<DispatchTaskStepSnapshot>> GetStepsAsync(string id, CancellationToken token) => inner.GetStepsAsync(id, token);
    public Task<IReadOnlyList<ActiveDispatchTask>> GetPendingAcknowledgementsAsync(CancellationToken token) => inner.GetPendingAcknowledgementsAsync(token);
    public Task MarkAcknowledgedAsync(string id, CancellationToken token) => inner.MarkAcknowledgedAsync(id, token);
    public Task RecordAcknowledgementFailureAsync(string id, string reason, CancellationToken token) => inner.RecordAcknowledgementFailureAsync(id, reason, token);
}
