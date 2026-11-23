using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Runtime;
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

// D2 (status sync): vendor status queries run before the map execution gate is taken, so a silent vehicle
// does not block another gate operation while its status request is pending.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var root = Path.Combine(Path.GetTempPath(), "agv-status-prefetch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db);
var orchestration = new DispatchOrchestrationRepository(db);
var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromSeconds(30)));
var map = new ActiveMapRuntime();
map.Replace(new("M", "V1", new("fixture", "synthetic", "Cartesian", "m",
    [new("BUS-A", 0, 0, MapNodeType.Station, true, [], "A"), new("BUS-B", 10, 0, MapNodeType.Station, true, [], "B")],
    [new("AB", "BUS-A", "BUS-B", 10, 1, true, true, ["R"])], [new("R", TrafficResourceType.NarrowAisle)]), DateTimeOffset.UtcNow));
var outbox = new AgvCommandOutboxRepository(db, map);

var created = await tasks.TryCreateAsync("ST", "DUMP", 50, default);
await tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity
{
    Stage = (int)DispatchTaskStage.GoingToPickup, VendorTaskId = "VENDOR-1", VehicleId = "V"
}).Where(t => t.TaskId == created.Task.TaskId).ExecuteCommandAsync();

var gateway = DispatchProxy.Create<IAgvGateway, StatusGateway>();
var fake = (StatusGateway)gateway;
var gate = new MapExecutionGate();
gate.SetMode(MapExecutionMode.Running);
var orchestrator = new DispatchTaskOrchestrator(NullLogger<DispatchTaskOrchestrator>.Instance,
    new DispatchOrchestratorOptions(), orchestration, outbox, gateway, new AgvStateCache(new AgvOptions()), map,
    new MapOptions(), new NoRoutes(), traffic, gate);
var cycle = typeof(DispatchTaskOrchestrator).GetMethod("RunCycleUnderGateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
var running = (Task)cycle.Invoke(orchestrator, [CancellationToken.None])!;

await fake.StatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
Check(!running.IsCompleted, "the cycle is still waiting on the vendor status request");
using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var lease = await gate.EnterAsync(MapExecutionOperation.ExistingWork, probe.Token);
Check(lease is not null, "another map operation enters the gate while a vendor status request is pending");
lease!.Dispose();
fake.StatusRelease.SetResult();
await running.WaitAsync(TimeSpan.FromSeconds(10));
Check(fake.StatusQueries == 1, "the executing vendor task is queried once for the cycle");
Console.WriteLine($"PASS: {checks} status prefetch checks; isolated SQLite and fake vehicle gateway only.");

public sealed class NoRoutes : IRoutePlanner
{
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) => null;
}
public class StatusGateway : DispatchProxy
{
    public readonly TaskCompletionSource StatusStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource StatusRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int StatusQueries;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetConnectionStatusAsync" => Task.FromResult(new DeviceConnectionStatus("fleet", "fixture", ConnectionState.Online, DateTimeOffset.UtcNow)),
        "GetVehiclesAsync" => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([]),
        "GetTransportTaskStatusAsync" => QueryAsync((string)args![0]!),
        _ => throw new NotSupportedException("The prefetch test only reads vendor status: " + method?.Name)
    };
    private async Task<AgvTransportTaskStatus?> QueryAsync(string vendorTaskId)
    {
        StatusQueries++;
        StatusStarted.TrySetResult();
        await StatusRelease.Task;
        return new AgvTransportTaskStatus(vendorTaskId, "BUSINESS", AgvTransportTaskState.GoingToDestination, "V", DateTimeOffset.UtcNow);
    }
}
