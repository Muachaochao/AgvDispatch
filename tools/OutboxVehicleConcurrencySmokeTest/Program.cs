using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging;

var checks = 0;
void Check(bool passed, string name)
{
    if (!passed) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var root = Path.Combine(Path.GetTempPath(), "agv-outbox-concurrency-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "private.db") });
await new DatabaseInitializer(db, new TrafficResourceCatalog([])).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db);
var outbox = new AgvCommandOutboxRepository(db);
var vehicles = new[] { "CAR-BLOCKED", "CAR-READY-1", "CAR-READY-2" };

foreach (var vehicle in vehicles)
{
    var created = await tasks.TryCreateAsync(vehicle + "-ORIGIN", "DUMPER", 0, default);
    await tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
    var step = (await tasks.GetStepsAsync(created.Task.TaskId, default))[0];
    if (!await outbox.TryPrepareTransportStepAsync(created.Task.TaskId, step.StepId, vehicle, ["A", "B"], new("Wait"), default))
        throw new Exception("Could not prepare isolated command for " + vehicle);
}

var gateway = new ConcurrentFleet();
var gate = new MapExecutionGate();
gate.SetMode(MapExecutionMode.Running);
using var dispatcher = new AgvCommandOutboxDispatcher(new ConsoleLogger<AgvCommandOutboxDispatcher>(),
    new AgvCommandOutboxOptions { MaximumConcurrentVehicles = 3 }, outbox, gateway, gate);
var dispatch = typeof(AgvCommandOutboxDispatcher).GetMethod("DispatchDueCommandsAsync",
    BindingFlags.Instance | BindingFlags.NonPublic)!;

var cycle = (Task)dispatch.Invoke(dispatcher, [CancellationToken.None])!;
var healthyCompletedWhileBlocked = false;
try
{
    await gateway.AllVehiclesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await gateway.ReadyVehiclesCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    healthyCompletedWhileBlocked = gateway.Completed.Contains("CAR-READY-1") &&
        gateway.Completed.Contains("CAR-READY-2");
}
finally
{
    // Always let the blocked send finish before the test disposes the shared
    // SqlSugar scope, otherwise a failed assertion masks the real result with
    // "cannot rollback - no transaction is active".
    gateway.ReleaseBlocked.TrySetResult();
    await cycle.WaitAsync(TimeSpan.FromSeconds(5));
}

Check(gateway.Started.Contains("CAR-BLOCKED"),
    "blocked vehicle and healthy vehicles are all admitted by the cycle");
Check(gateway.MaximumActiveSendCount >= 3,
    "different vehicles send concurrently instead of sharing one head-of-line loop");
Check(healthyCompletedWhileBlocked,
    "two healthy vehicles complete the same cycle while the third vehicle is still blocked");
var rows = db.Queryable<AgvCommandOutboxEntity>().ToList();
Check(rows.Count == 3 && rows.All(row => row.Status == (int)AgvCommandStatus.Sent),
    "once the blocked vehicle is released, its original durable command also completes");
Check(rows.Select(row => row.CommandId).Distinct().Count() == 3,
    "concurrent vehicle groups never duplicate or merge durable command identities");

Console.WriteLine($"Outbox vehicle concurrency: {checks} checks passed. Isolated SQLite and synthetic gateway only.");

sealed class ConcurrentFleet : IAgvGateway
{
    private readonly object sync = new();
    private readonly List<string> started = [];
    private readonly List<string> completed = [];
    private int activeSendCount;
    private int maximumActiveSendCount;

    public TaskCompletionSource ReleaseBlocked { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadyVehiclesStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReadyVehiclesCompleted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllVehiclesStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Started
    {
        get { lock (sync) return started.ToArray(); }
    }

    public IReadOnlyList<string> Completed
    {
        get { lock (sync) return completed.ToArray(); }
    }

    public int ActiveSendCount => Volatile.Read(ref activeSendCount);
    public int MaximumActiveSendCount => Volatile.Read(ref maximumActiveSendCount);

    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DeviceConnectionStatus("FLEET", "Synthetic", ConnectionState.Online,
            DateTimeOffset.Now, null));

    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgvVehicleStatus>>(
        [
            new("CAR-BLOCKED", ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now),
            new("CAR-READY-1", ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now),
            new("CAR-READY-2", ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now)
        ]);

    public async Task<string> CreateTransportTaskAsync(string id, string? vehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        lock (sync)
        {
            started.Add(vehicleId);
            if (started.Contains("CAR-READY-1") && started.Contains("CAR-READY-2"))
                ReadyVehiclesStarted.TrySetResult();
            if (started.Contains("CAR-BLOCKED") && started.Contains("CAR-READY-1") &&
                started.Contains("CAR-READY-2"))
                AllVehiclesStarted.TrySetResult();
        }
        var active = Interlocked.Increment(ref activeSendCount);
        while (true)
        {
            var maximum = Volatile.Read(ref maximumActiveSendCount);
            if (active <= maximum ||
                Interlocked.CompareExchange(ref maximumActiveSendCount, active, maximum) == maximum)
                break;
        }

        try
        {
            if (vehicleId == "CAR-BLOCKED")
                await ReleaseBlocked.Task.WaitAsync(ct);
            else
            {
                // Hold healthy sends in flight until the blocked vehicle has
                // also entered the gateway. This makes overlap deterministic
                // without serializing the actual send path.
                await AllVehiclesStarted.Task.WaitAsync(ct);
                await Task.Delay(150, ct);
            }
            lock (sync)
            {
                completed.Add(vehicleId);
                if (completed.Contains("CAR-READY-1") && completed.Contains("CAR-READY-2"))
                    ReadyVehiclesCompleted.TrySetResult();
            }
            return id;
        }
        finally
        {
            Interlocked.Decrement(ref activeSendCount);
        }
    }

    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CancelTaskAsync(string id, CancellationToken ct) =>
        throw new NotSupportedException();
}

internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        if (exception is not null) Console.Error.WriteLine(exception);
    }
}
