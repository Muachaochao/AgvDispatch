using System.Text.Json;
using AgvDispatch.Application.Abstractions;
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

var checks = 0;
void Check(bool valid, string label)
{
    if (!valid) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var directory = Path.Combine(Path.GetTempPath(), "agv-obstacle-auto-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var clock = new ManualClock(start);
    using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(directory, "test.db") });
    var definition = new MapDefinition("MAP-O", "obstacle-auto", "RobotWorld", "m",
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
    await new DatabaseInitializer(db, catalog).InitializeAsync(default);
    var gate = new MapExecutionGate();
    gate.SetMode(MapExecutionMode.Running);
    var closures = new RoadClosureService(db, map, gate);
    await closures.InitializeAsync(default);
    var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromMinutes(1)), clock);
    var agvOptions = new AgvOptions
    {
        Enabled = true,
        AllowCommands = true,
        StaleAfterMs = 10000,
        Robots = [new AgvRobotOptions { VehicleId = "A", IpAddress = "127.0.0.1", Enabled = true }]
    };
    var states = new AgvStateCache(agvOptions, clock);
    var tasks = new DispatchOrchestrationRepository(db);
    var chains = new AgvTransportChainLookup(db);
    var gateway = new RecoveryGateway();
    var timeout = new Preconditions();
    var body = new Envelope();
    var recoveryOptions = new ObstacleRecoveryOptions
    {
        Enabled = true,
        AutomaticDetourEnabled = true,
        DetourAfterSeconds = 5,
        WarningAfterSeconds = 10,
        AlarmAfterSeconds = 30,
        ClosureSuggestionAfterSeconds = 60,
        StableStopObservationMs = 500
    };
    var mapOptions = new MapOptions { PositionStaleSeconds = 30, MinimumLocalizationConfidence = 0.5 };
    var dispatch = new DispatchOrchestratorOptions
    {
        StandbySites = ["S"],
        DumperWaitingSite = "X",
        DumperDumpingSite = "X"
    };
    var store = new ObstacleRecoveryStore(db, recoveryOptions, clock);
    var coordinator = new ObstacleRecoveryCoordinator(db, states, map, tasks, chains, closures,
        traffic, body, gate, gateway, timeout, store, recoveryOptions, mapOptions, dispatch,
        clock, NullLogger<ObstacleRecoveryCoordinator>.Instance);

    var taskId = "TASK-1";
    var stepId = "STEP-1";
    await SeedTask(db, taskId, stepId);

    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    var row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(row.State == ObstacleRecoveryStates.Blocked && gateway.CancelCalls == 0 &&
          gateway.RecoverySendCalls == 0, "障碍初期只观察，不取消也不重发");

    clock.Advance(TimeSpan.FromSeconds(6));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(row.State == ObstacleRecoveryStates.DetourReady &&
          Decode(row.CandidatePathJson).SequenceEqual(["S", "D", "X"]) &&
          gateway.CancelCalls == 0, "受阻5秒后生成避开S→H的安全候选，等待停稳门槛");

    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 1 && gateway.RecoverySendCalls == 0 &&
          row.State == ObstacleRecoveryStates.AwaitingOriginalTerminal,
          "连续停稳和安全条件通过后，只取消一次原3066任务");
    await db.Updateable<DispatchTaskEntity>()
        .SetColumns(item => new DispatchTaskEntity
        {
            Stage = (int)DispatchTaskStage.Faulted,
            ErrorMessage = "synthetic orchestrator observed expected vendor cancellation"
        }).Where(item => item.TaskId == taskId).ExecuteCommandAsync();
    await db.Updateable<DispatchTaskStepEntity>()
        .SetColumns(item => new DispatchTaskStepEntity { Status = (int)DispatchTaskStepStatus.Failed })
        .Where(item => item.StepId == stepId).ExecuteCommandAsync();

    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.RecoverySendCalls == 0 && row.OriginalTerminalAtUtc is not null &&
          row.State == ObstacleRecoveryStates.AwaitingOriginalTerminal,
          "原链终态后先重新累计停稳，不在同一周期发送替代路线");

    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.RecoverySendCalls == 0 &&
          row.State == ObstacleRecoveryStates.SafetyHold,
          "取消后的第一次安全复核只开始停稳计时，不立即发送");

    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    var reboundTask = db.Queryable<DispatchTaskEntity>().Single(item => item.TaskId == taskId);
    var reboundStep = db.Queryable<DispatchTaskStepEntity>().Single(item => item.StepId == stepId);
    var reboundCommand = db.Queryable<AgvCommandOutboxEntity>()
        .OrderByDescending(item => item.CreatedAt)
        .First(item => item.TaskId == taskId && item.StepId == stepId &&
                       item.Status == (int)AgvCommandStatus.Sent);
    Check(gateway.RecoverySendCalls == 1 && gateway.SentPath.SequenceEqual(["S", "D", "X"]),
          "原任务终态后只发送一条替代3066任务链");
    Check(reboundTask.TaskId == taskId && reboundTask.VendorTaskId == gateway.NewVendorTaskId &&
          reboundStep.TaskId == taskId && reboundStep.VendorTaskId == gateway.NewVendorTaskId,
          "替代导航绑定回原业务任务和原步骤身份");
    Check(reboundCommand.VendorCommandId == gateway.NewVendorTaskId && row.State == ObstacleRecoveryStates.Executing,
          "命令、恢复记录和新厂家任务编号保持一致");
    Check(db.Queryable<ExecutionEvidenceEntity>().Any(item =>
            item.TaskId == taskId && item.Kind == "ObstacleAutomaticDetourSent"),
          "自动改道发送写入可审计证据");

    await coordinator.ObserveOnceAsync(default);
    Check(gateway.CancelCalls == 1 && gateway.RecoverySendCalls == 1,
          "后续周期不会重复取消或重复发送替代导航");

    var originalRecoveryId = row.RecoveryId;
    var originalRevision = row.Revision;
    row.RecoveryId = Guid.NewGuid().ToString("N");
    row.State = ObstacleRecoveryStates.Blocked;
    row.Message = "same task/step blocked again after restart";
    row.Revision = 1;
    await store.SaveAsync(row, default);
    var reopened = db.Queryable<ObstacleRecoveryEntity>()
        .Where(item => item.TaskId == taskId && item.StepId == stepId)
        .ToList();
    Check(reopened.Count == 1 && reopened[0].RecoveryId == originalRecoveryId &&
          reopened[0].Revision == originalRevision + 1,
          "同一任务步骤再次受阻时复用唯一恢复记录，不触发复合唯一键异常");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),
            StringComparison.OrdinalIgnoreCase))
        Directory.Delete(directory, true);
}
Console.WriteLine($"PASS: {checks} obstacle automatic recovery checks; isolated database and synthetic dispatcher only.");

static async Task SeedTask(ISqlSugarClient db, string taskId, string stepId)
{
    var now = DateTime.UtcNow;
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
    var payload = new AgvTransportCommandPayload(stepId, "A", "S", "X", ["S", "H", "X"], null);
    await db.Insertable(new AgvCommandOutboxEntity
    {
        CommandId = "CMD-1",
        IdempotencyKey = "TEST:" + stepId,
        TaskId = taskId,
        StepId = stepId,
        VehicleId = "A",
        CommandType = (int)AgvCommandType.Transport,
        Status = (int)AgvCommandStatus.Sent,
        PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
        SentAt = now,
        VendorCommandId = stepId
    }).ExecuteCommandAsync();
}

static string[] Decode(string json) =>
    JsonSerializer.Deserialize<string[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

static AgvRobotTelemetry Telemetry(DateTimeOffset at, bool blocked) =>
    new(0, 0, 0, .99, "S", null, 80, blocked, false, "Running", null, null, null,
        [], [], at) { VelocityX = 0, VelocityY = 0, AngularVelocity = 0, Charging = false };

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset current = start;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan span) => current += span;
}

sealed class RecoveryGateway : IAgvGateway, IAgvObstacleRecoveryGateway
{
    public int CancelCalls { get; private set; }
    public int RecoverySendCalls { get; private set; }
    public string? NewVendorTaskId { get; private set; }
    public IReadOnlyList<string> SentPath { get; private set; } = [];
    private AgvTransportTaskState original = AgvTransportTaskState.GoingToDestination;
    public Task<string> CreateTransportTaskAsync(string businessTaskId, string? preferredVehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("通用发送口在受阻状态必须被拒绝。");
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId,
        CancellationToken cancellationToken) => Status(vendorTaskId);
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId, string vehicleId,
        IReadOnlyList<string> originalPath, CancellationToken cancellationToken) => Status(vendorTaskId);
    public Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken)
    {
        CancelCalls++;
        original = AgvTransportTaskState.Cancelled;
        return Task.CompletedTask;
    }
    public Task<string> CreateObstacleRecoveryTransportTaskAsync(string newVendorTaskId, string vehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, DateTimeOffset blockedSince,
        string originalVendorTaskId, CancellationToken cancellationToken)
    {
        RecoverySendCalls++;
        NewVendorTaskId = newVendorTaskId;
        SentPath = path.ToArray();
        return Task.FromResult(newVendorTaskId);
    }
    private Task<AgvTransportTaskStatus?> Status(string id) =>
        Task.FromResult<AgvTransportTaskStatus?>(new(id, id, original, "A", DateTimeOffset.UtcNow, "synthetic"));
}

sealed class Preconditions : ITransportTimeoutRecoveryService
{
    public Task<TransportTimeoutRecoveryAssessment?> CaptureAsync(DispatchTaskWorkItem task,
        AgvTransportTaskStatus status, CancellationToken cancellationToken) =>
        Task.FromResult<TransportTimeoutRecoveryAssessment?>(null);
    public Task<TransportTimeoutRecoverySnapshot?> GetAsync(string taskId, CancellationToken cancellationToken) =>
        Task.FromResult<TransportTimeoutRecoverySnapshot?>(null);
    public Task<TransportTimeoutRecoveryResult> ExecuteAsync(TrafficUserContext user,
        TransportTimeoutRecoveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new TransportTimeoutRecoveryResult(false, "not used"));
    public Task<AgvTransportTaskStatus?> GetReplacementStatusAsync(DispatchTaskWorkItem task,
        CancellationToken cancellationToken) => Task.FromResult<AgvTransportTaskStatus?>(null);
    public Task<string?> ValidateReplacementPreconditionsAsync(string taskId, string stepId,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class Envelope : ITrafficBodyEnvelopeProvider
{
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct) =>
        Task.FromResult<TrafficBodyEnvelope?>(new("SIM-A", vehicleId, "RobotWorld", 0.47, true, true));
}
