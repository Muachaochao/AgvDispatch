using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

var checks = 0;
void Check(bool valid, string label)
{
    if (!valid) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var directory = Path.Combine(Path.GetTempPath(), "agv-obstacle-r7-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    await CancelPendingOrchestratorAsync(directory);
    Console.WriteLine($"PASS: {checks} round-7 obstacle recovery checks; isolated database and synthetic gateway only.");
}
finally
{
    try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
}

// O1: the coordinator cancelled the original vendor task. The ordinary
// orchestrator then observes the vendor's Cancelled status on its 1 s pass. It
// must not finalise the business step while the replacement is still pending.
async Task CancelPendingOrchestratorAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var definition = new MapDefinition("MAP-O", "obstacle-r7", "RobotWorld", "m",
        [
            new("S", 0, 0, MapNodeType.Station, true, [], "S"),
            new("H", 1, 0, MapNodeType.Waypoint, true, [], "H"),
            new("D", 0, 1, MapNodeType.Waypoint, true, [], "D"),
            new("BUS-X", 1, 1, MapNodeType.Station, true, [], "X")
        ],
        [
            new("E-SH", "S", "H", 1, 1, false, true, []),
            new("E-HX", "H", "BUS-X", 1, 1, false, true, []),
            new("E-SD", "S", "D", 1, 1, false, true, []),
            new("E-DX", "D", "BUS-X", 1, 1, false, true, [])
        ], []);
    var map = new ActiveMapRuntime();
    map.Replace(new("MAP-O", "V1", definition, start));
    var catalog = new TrafficResourceCatalog(MapCorridorResources.AllResources(definition));
    using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "o1.db") });
    await new DatabaseInitializer(db, catalog).InitializeAsync(default);
    var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromMinutes(1)));
    var states = new AgvStateCache(new AgvOptions
    {
        Enabled = true,
        AllowCommands = true,
        StaleAfterMs = 10000,
        Robots = [new AgvRobotOptions { VehicleId = "A", IpAddress = "127.0.0.1", Enabled = true }]
    });
    var gateway = new VendorCancelledGateway();
    var orchestrator = new DispatchTaskOrchestrator(NullLogger<DispatchTaskOrchestrator>.Instance,
        new(), new DispatchOrchestrationRepository(db), new AgvCommandOutboxRepository(db),
        gateway, states, map, new(), null!, traffic, null!,
        timeoutRecovery: new TransportTimeoutRecoveryService(db, states, map,
            new AgvTransportChainLookup(db), traffic, null, new()));

    const string taskId = "TASK-O1";
    const string stepId = "STEP-O1";
    await SeedExecutingTransportAsync(db, taskId, stepId, start);
    await SeedRecoveryAsync(db, taskId, stepId, definition, start, cancelRequested: true);

    await Sync(orchestrator);
    var step = db.Queryable<DispatchTaskStepEntity>().Single(item => item.StepId == stepId);
    var task = db.Queryable<DispatchTaskEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.StatusQueries > 0, "编排器通过真实同步路径读取了厂家取消状态");
    Check(step.Status == (int)DispatchTaskStepStatus.Executing &&
          task.Stage == (int)DispatchTaskStage.GoingToPickup,
        "障碍协调器已请求取消且替代导航未绑定时，厂家Cancelled不终结业务步骤");

    // Control: the same vendor observation without a coordinator cancel is still
    // finalised as before, so the guard is the only difference.
    await db.Updateable<ObstacleRecoveryEntity>()
        .SetColumns(item => new ObstacleRecoveryEntity { State = ObstacleRecoveryStates.Recovered })
        .Where(item => item.TaskId == taskId).ExecuteCommandAsync();
    await Sync(orchestrator);
    step = db.Queryable<DispatchTaskStepEntity>().Single(item => item.StepId == stepId);
    task = db.Queryable<DispatchTaskEntity>().Single(item => item.TaskId == taskId);
    Check(step.Status != (int)DispatchTaskStepStatus.Executing &&
          task.Stage == (int)DispatchTaskStage.Cancelled,
        "没有待决障碍取消时，厂家Cancelled仍按原逻辑终结步骤");
}

static async Task SeedExecutingTransportAsync(ISqlSugarClient db, string taskId, string stepId,
    DateTimeOffset start)
{
    var now = start.UtcDateTime;
    await db.Insertable(new DispatchTaskEntity
    {
        TaskId = taskId,
        StationId = "CNC-001",
        VehicleId = "A",
        VendorTaskId = stepId,
        Stage = (int)DispatchTaskStage.GoingToPickup,
        Priority = 0,
        CreatedAt = now,
        UpdatedAt = now
    }).ExecuteCommandAsync();
    await db.Insertable(new DispatchTaskStepEntity
    {
        StepId = stepId,
        TaskId = taskId,
        Sequence = 1,
        StepType = (int)DispatchTaskStepType.GoToPickup,
        Status = (int)DispatchTaskStepStatus.Executing,
        SourceSite = "S",
        DestinationSite = "BUS-X",
        VehicleId = "A",
        VendorTaskId = stepId,
        CreatedAt = now,
        UpdatedAt = now,
        StartedAt = now
    }).ExecuteCommandAsync();
}

static async Task SeedRecoveryAsync(ISqlSugarClient db, string taskId, string stepId,
    MapDefinition definition, DateTimeOffset start, bool cancelRequested)
{
    var now = start.UtcDateTime;
    await db.Insertable(new ObstacleRecoveryEntity
    {
        RecoveryId = Guid.NewGuid().ToString("N"),
        TaskId = taskId,
        StepId = stepId,
        VehicleId = "A",
        OriginalVendorTaskId = stepId,
        State = ObstacleRecoveryStates.AwaitingOriginalTerminal,
        Message = "synthetic cancel pending",
        NextAction = "synthetic",
        BlockedSinceUtc = now,
        OriginalPathJson = "[\"S\",\"H\",\"X\"]",
        CandidatePathJson = "[]",
        BlockedEdgeIdsJson = "[]",
        MapId = "MAP-O",
        MapVersionId = "V1",
        DefinitionHash = EngineeringRuntimeIdentity.MapHash(definition),
        CancelRequestedAtUtc = cancelRequested ? now : null,
        CancelAttemptedAtUtc = cancelRequested ? now : null,
        CancelAcknowledgedAtUtc = cancelRequested ? now : null,
        Revision = 1,
        CreatedAt = now,
        UpdatedAt = now
    }).ExecuteCommandAsync();
}

static async Task Sync(DispatchTaskOrchestrator orchestrator) =>
    await (Task)typeof(DispatchTaskOrchestrator)
        .GetMethod("SynchronizeExecutingTasksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(orchestrator, [CancellationToken.None])!;

// Vendor view after the coordinator's cancel: the original chain reports Cancelled.
sealed class VendorCancelledGateway : IAgvGateway
{
    public int StatusQueries { get; private set; }

    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId,
        CancellationToken cancellationToken)
    {
        StatusQueries++;
        return Task.FromResult<AgvTransportTaskStatus?>(new(vendorTaskId, vendorTaskId,
            AgvTransportTaskState.Cancelled, "A", DateTimeOffset.UtcNow, "synthetic vendor cancelled"));
    }

    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId, string vehicleId,
        IReadOnlyList<string> originalPath, CancellationToken cancellationToken) =>
        GetTransportTaskStatusAsync(vendorTaskId, cancellationToken);

    public Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<string> CreateTransportTaskAsync(string businessTaskId, string? preferredVehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("synthetic gateway does not send navigation.");

    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
