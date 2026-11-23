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
    await MidRoadStopIsReportedAsync(directory);
    await NoRouteNeverFreezesBeforeCancelAsync(directory);
    await AcceleratedRateStopEvidenceAsync(directory);
    await PreSendRejectionRetriesAsync(directory);
    await UncertainSendIsNotResentAsync(directory);
    Console.WriteLine($"PASS: {checks} round-7 obstacle recovery checks; isolated database and synthetic gateway only.");
}
finally
{
    try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
}

// O2: a vehicle stopped between stations cannot be planned from a historical
// station. It must be reported as a distinct state, escalate to Alarm on its
// own timer, and never freeze the business task. Back on a trusted station the
// same obstacle still yields the normal detour candidate.
async Task MidRoadStopIsReportedAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o2", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O2", "STEP-O2");
    h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(start, blocked: true), TimeSpan.Zero);
    await h.Coordinator.ObserveOnceAsync(default);
    for (var second = 5; second <= 125; second += 5)
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: true,
            x: 0.5, y: 0.2, current: null), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
    }
    var row = RecoveryRow(h.Db, "TASK-O2");
    var expectedMidRoadState = row.State;
    Check(expectedMidRoadState == ObstacleRecoveryStates.Alarm, "路段中间受阻超过报警时间后升为报警状态");
    Check(row.State != ObstacleRecoveryStates.Frozen && row.CancelRequestedAtUtc is null &&
          h.Gateway.CancelCalls == 0 && h.Gateway.RecoverySendCalls == 0,
        "路段中间受阻不冻结、不取消原导航、不发送替代路线");
    var task = TaskRow(h.Db, "TASK-O2");
    var step = StepRow(h.Db, "STEP-O2");
    Check(task.Stage == (int)DispatchTaskStage.GoingToPickup &&
          step.Status == (int)DispatchTaskStepStatus.Executing,
        "路段中间受阻期间业务任务和步骤保持执行中");
    Check(row.NextAction.Contains("站点"), "路段中间报警明确提示需要停在可信站点");

    h.Clock.SetTo(start.AddSeconds(130));
    h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await h.Coordinator.ObserveOnceAsync(default);
    row = RecoveryRow(h.Db, "TASK-O2");
    Check(row.State == ObstacleRecoveryStates.DetourReady &&
          ObstacleHarnessFactory.Decode(row.CandidatePathJson).SequenceEqual(["S", "D", "X"]) &&
          h.Gateway.CancelCalls == 0,
        "回到可信站点S后，同一障碍仍生成S→D→X替代候选且只读");
}

// O3: before a cancellation a route-less obstacle only warns and alarms. It must
// not change the task stage or freeze. When the obstacle clears, the original
// navigation is still live and the record closes without touching the task.
async Task NoRouteNeverFreezesBeforeCancelAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o3", ObstacleHarnessFactory.StationGraph(false),
        start, "TASK-O3", "STEP-O3");
    h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(start, blocked: true), TimeSpan.Zero);
    await h.Coordinator.ObserveOnceAsync(default);
    for (var second = 5; second <= 305; second += 5)
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
    }
    var row = RecoveryRow(h.Db, "TASK-O3");
    var task = TaskRow(h.Db, "TASK-O3");
    var step = StepRow(h.Db, "STEP-O3");
    Check(row.State == ObstacleRecoveryStates.Alarm && row.State != ObstacleRecoveryStates.Frozen,
        "无替代路线超过300秒只升为报警，不冻结恢复记录");
    Check(task.Stage == (int)DispatchTaskStage.GoingToPickup &&
          step.Status == (int)DispatchTaskStepStatus.Executing && h.Gateway.CancelCalls == 0,
        "取消前无路线不改变业务任务阶段、步骤状态，也不取消原导航");

    h.Clock.SetTo(start.AddSeconds(310));
    h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: false), TimeSpan.Zero);
    await h.Coordinator.ObserveOnceAsync(default);
    row = RecoveryRow(h.Db, "TASK-O3");
    task = TaskRow(h.Db, "TASK-O3");
    Check(row.State == ObstacleRecoveryStates.Recovered &&
          task.Stage == (int)DispatchTaskStage.GoingToPickup && h.Gateway.CancelCalls == 0,
        "障碍清除后记录关闭，原导航仍在执行，任务不被冻结或取消");
}

// O4: stop evidence must not depend on the coordinator's wall-clock poll cadence.
// At an accelerated simulation rate the telemetry frames are many seconds apart in
// business time; consecutive stationary frames must still complete the evidence.
async Task AcceleratedRateStopEvidenceAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o4", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O4", "STEP-O4");
    h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(start, blocked: true), TimeSpan.Zero);
    await h.Coordinator.ObserveOnceAsync(default);
    for (var second = 8; second <= 128; second += 8)
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
    }
    var row = RecoveryRow(h.Db, "TASK-O4");
    Check(h.Gateway.CancelCalls == 1 && row.CancelRequestedAtUtc == start.AddSeconds(120).UtcDateTime,
        "遥测每8秒一帧（业务时间，类似8倍速）时停稳证据仍成立，并在120秒取消一次");
    Check(row.State == ObstacleRecoveryStates.AwaitingOriginalTerminal && h.Gateway.RecoverySendCalls == 0,
        "取消后停稳证据被清空，替代路线仍须在原链终态后重新累计");
}

// O5: a refusal that happens before any command reaches the vehicle clears the send
// intent and retries after a backoff; the original task and route stay intact.
async Task PreSendRejectionRetriesAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o5a", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O5A", "STEP-O5A");
    h.Gateway.OriginalState = AgvTransportTaskState.Cancelled;
    h.Gateway.NextSendFailure = new AgvRecoverySendRejectedException("synthetic refusal before send");
    var second = 0;
    async Task Tick()
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
    }
    for (; second <= 200 && h.Gateway.RecoverySendCalls == 0; second++)
        await Tick();
    var row = RecoveryRow(h.Db, "TASK-O5A");
    Check(h.Gateway.RecoverySendCalls == 1 && row.NewVendorTaskId is null && row.SendAttemptedAtUtc is null &&
          row.State == ObstacleRecoveryStates.DetourReady && row.Message.Contains("发送前被拒绝"),
        "车端发送前拒绝时清除发送意图，回到可重试的绕行就绪状态");
    Check(TaskRow(h.Db, "TASK-O5A").Stage == (int)DispatchTaskStage.GoingToPickup,
        "发送前拒绝不改变原业务任务阶段");

    for (var extra = 0; extra < 2; extra++, second++)
        await Tick();
    Check(h.Gateway.RecoverySendCalls == 1, "退避期内不重复尝试发送");
    for (; second <= 260 && h.Gateway.RecoverySendCalls < 2; second++)
        await Tick();
    row = RecoveryRow(h.Db, "TASK-O5A");
    var task = TaskRow(h.Db, "TASK-O5A");
    Check(h.Gateway.RecoverySendCalls == 2 && row.State == ObstacleRecoveryStates.Executing &&
          row.NewVendorTaskId is not null && task.VendorTaskId == row.NewVendorTaskId,
        "退避结束后重新核验并发送一次，成功后绑定原业务任务");
}

// O5: a failure after the send attempt is uncertain. It must never be resent by
// this coordinator while the replacement identity is unresolved.
async Task UncertainSendIsNotResentAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o5b", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O5B", "STEP-O5B");
    h.Gateway.OriginalState = AgvTransportTaskState.Cancelled;
    h.Gateway.NextSendFailure = new TimeoutException("synthetic response lost after the command was sent");
    var second = 0;
    async Task Tick()
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
    }
    for (; second <= 200 && h.Gateway.RecoverySendCalls == 0; second++)
        await Tick();
    var row = RecoveryRow(h.Db, "TASK-O5B");
    Check(h.Gateway.RecoverySendCalls == 1 && row.NewVendorTaskId is not null &&
          row.Message.Contains("结果不确定"),
        "发送后超时记为结果不确定，并保留新厂家编号");
    for (var extra = 0; extra < 90; extra++, second++)
        await Tick();
    Check(h.Gateway.RecoverySendCalls == 1,
        "结果不确定后不重新生成编号、不重复发送替代导航");
}

static ObstacleRecoveryEntity RecoveryRow(ISqlSugarClient db, string taskId) =>
    db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);

static DispatchTaskEntity TaskRow(ISqlSugarClient db, string taskId) =>
    db.Queryable<DispatchTaskEntity>().Single(item => item.TaskId == taskId);

static DispatchTaskStepEntity StepRow(ISqlSugarClient db, string stepId) =>
    db.Queryable<DispatchTaskStepEntity>().Single(item => item.StepId == stepId);

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
