using System.Reflection;
using System.Text.Json;
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
    await BoundCycleClosesAndReopensAsync(directory);
    await ClearedOriginalRouteResumesAsync(directory);
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
    var replacementCommand = h.Db.Queryable<AgvCommandOutboxEntity>()
        .Where(item => item.TaskId == "TASK-O5A" && item.VendorCommandId == row.NewVendorTaskId)
        .First();
    var replacementPayload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(
        replacementCommand!.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(replacementPayload is { BusinessStepId: "STEP-O5A", BusinessTaskId: var businessTask } &&
          businessTask == row.NewVendorTaskId,
        "替代3066命令载荷携带原业务步骤号，供到站释放与分段交接核对");
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
    row = RecoveryRow(h.Db, "TASK-O5B");
    Check(row.State == ObstacleRecoveryStates.Uncertain && row.NewVendorTaskId is not null &&
          TaskRow(h.Db, "TASK-O5B").VendorTaskId != row.NewVendorTaskId,
        "结果不确定状态保持粘滞，直到只读对账和现场处置，不被改写为执行中");

    // A crash after the send intent but before the bind: the same record restarts
    // uncertain, not executing, and is not sent again.
    var crash = await ObstacleHarnessFactory.BuildAsync(root, "o6b", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O6B", "STEP-O6B");
    var crashed = new ObstacleRecoveryEntity
    {
        RecoveryId = Guid.NewGuid().ToString("N"),
        TaskId = "TASK-O6B",
        StepId = "STEP-O6B",
        VehicleId = "A",
        OriginalVendorTaskId = "STEP-O6B",
        State = ObstacleRecoveryStates.SendIntent,
        Message = "synthetic persisted send intent",
        NextAction = "synthetic",
        BlockedSinceUtc = start.UtcDateTime,
        OriginalPathJson = "[\"S\",\"H\",\"X\"]",
        CandidatePathJson = "[\"S\",\"D\",\"X\"]",
        BlockedEdgeIdsJson = "[\"E-SH\"]",
        MapId = "MAP-O",
        MapVersionId = "V1",
        DefinitionHash = EngineeringRuntimeIdentity.MapHash(ObstacleHarnessFactory.StationGraph(true)),
        NewVendorTaskId = "OBSTACLERECOVERY-CRASHED",
        SendAttemptedAtUtc = start.UtcDateTime,
        CancelRequestedAtUtc = start.UtcDateTime,
        CancelAttemptedAtUtc = start.UtcDateTime,
        CancelAcknowledgedAtUtc = start.UtcDateTime,
        OriginalTerminalAtUtc = start.UtcDateTime,
        Revision = 1,
        CreatedAt = start.UtcDateTime,
        UpdatedAt = start.UtcDateTime
    };
    await crash.Db.Insertable(crashed).ExecuteCommandAsync();
    crash.Clock.SetTo(start.AddSeconds(5));
    crash.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(crash.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await crash.Coordinator.ObserveOnceAsync(default);
    crashed = RecoveryRow(crash.Db, "TASK-O6B");
    Check(crashed.State == ObstacleRecoveryStates.Uncertain && crashed.NewVendorTaskId == "OBSTACLERECOVERY-CRASHED" &&
          crash.Gateway.RecoverySendCalls == 0,
        "发送意图已落库但未绑定时重启，记录为结果不确定且不重发");
}

// O7: a bound replacement belongs to one cycle. Completion of its step closes the
// cycle, and an obstacle that begins after the bind closes it and opens a new cycle
// for the same executing step, instead of being ignored.
async Task BoundCycleClosesAndReopensAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o7", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O7", "STEP-O7");
    h.Gateway.OriginalState = AgvTransportTaskState.Cancelled;
    var second = 0;
    async Task Tick(bool blocked)
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: blocked), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
        second++;
    }
    for (; second <= 200 && RecoveryState(h.Db, "TASK-O7") != ObstacleRecoveryStates.Executing; )
        await Tick(blocked: true);
    var bound = RecoveryRow(h.Db, "TASK-O7");
    Check(bound.State == ObstacleRecoveryStates.Executing && h.Gateway.RecoverySendCalls == 1 &&
          bound.NewVendorTaskId is not null, "替代导航绑定后进入执行中，只发送一次");
    for (var clear = 0; clear < 3; clear++)
        await Tick(blocked: false);
    Check(RecoveryRow(h.Db, "TASK-O7").State == ObstacleRecoveryStates.Executing,
        "绑定后车辆解除受阻，替代导航仍在执行，本轮保持执行中");

    await Tick(blocked: true);
    var closed = RecoveryRow(h.Db, "TASK-O7");
    Check(closed.State == ObstacleRecoveryStates.Recovered,
        "替代导航执行中出现新的受阻，关闭上一轮");
    await Tick(blocked: true);
    var reopened = RecoveryRow(h.Db, "TASK-O7");
    Check(reopened.State == ObstacleRecoveryStates.Blocked &&
          reopened.OriginalVendorTaskId == bound.NewVendorTaskId &&
          reopened.BlockedSinceUtc == start.AddSeconds(second - 2).UtcDateTime &&
          h.Gateway.RecoverySendCalls == 1,
        "新受阻开启新一轮，以当前替代编号为原导航，不重复发送");

    var done = await ObstacleHarnessFactory.BuildAsync(root, "o7b", ObstacleHarnessFactory.StationGraph(true),
        start, "TASK-O7B", "STEP-O7B");
    done.Gateway.OriginalState = AgvTransportTaskState.Cancelled;
    second = 0;
    var doneTick = done;
    for (; second <= 200 && RecoveryState(doneTick.Db, "TASK-O7B") != ObstacleRecoveryStates.Executing;)
    {
        done.Clock.SetTo(start.AddSeconds(second));
        done.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(done.Clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
        await done.Coordinator.ObserveOnceAsync(default);
        second++;
    }
    await done.Db.Updateable<DispatchTaskStepEntity>()
        .SetColumns(item => new DispatchTaskStepEntity { Status = (int)DispatchTaskStepStatus.Completed })
        .Where(item => item.StepId == "STEP-O7B").ExecuteCommandAsync();
    done.Clock.SetTo(start.AddSeconds(second));
    done.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(done.Clock.GetUtcNow(), blocked: false), TimeSpan.Zero);
    await done.Coordinator.ObserveOnceAsync(default);
    Check(RecoveryRow(done.Db, "TASK-O7B").State == ObstacleRecoveryStates.Recovered,
        "替代导航所属步骤完成后，本轮恢复记录关闭");
}

// O13: once the original chain is terminal and the vehicle is no longer blocked, the
// original remaining route may be sent again. Before, the inferred blocked edge kept
// it excluded forever, so the task froze with no route.
async Task ClearedOriginalRouteResumesAsync(string root)
{
    var start = new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);
    var h = await ObstacleHarnessFactory.BuildAsync(root, "o13", ObstacleHarnessFactory.StationGraph(false),
        start, "TASK-O13", "STEP-O13");
    h.Gateway.OriginalState = AgvTransportTaskState.Cancelled;
    // The earlier cancellation and the terminal observation are already persisted, as
    // they are after a coordinator cancel; the route then has no alternative.
    await h.Db.Insertable(new ObstacleRecoveryEntity
    {
        RecoveryId = Guid.NewGuid().ToString("N"),
        TaskId = "TASK-O13",
        StepId = "STEP-O13",
        VehicleId = "A",
        OriginalVendorTaskId = "STEP-O13",
        State = ObstacleRecoveryStates.AwaitingOriginalTerminal,
        Message = "synthetic earlier cancellation",
        NextAction = "synthetic",
        BlockedSinceUtc = start.UtcDateTime,
        OriginalPathJson = "[\"S\",\"H\",\"X\"]",
        CandidatePathJson = "[]",
        BlockedEdgeIdsJson = "[]",
        MapId = "MAP-O",
        MapVersionId = "V1",
        DefinitionHash = EngineeringRuntimeIdentity.MapHash(ObstacleHarnessFactory.StationGraph(false)),
        CancelRequestedAtUtc = start.UtcDateTime,
        CancelAttemptedAtUtc = start.UtcDateTime,
        CancelAcknowledgedAtUtc = start.UtcDateTime,
        OriginalTerminalAtUtc = start.UtcDateTime,
        Revision = 1,
        CreatedAt = start.UtcDateTime,
        UpdatedAt = start.UtcDateTime
    }).ExecuteCommandAsync();
    var second = 0;
    async Task Tick(bool blocked)
    {
        h.Clock.SetTo(start.AddSeconds(second));
        h.States.ApplyTelemetry("A", ObstacleHarnessFactory.Telemetry(h.Clock.GetUtcNow(), blocked: blocked), TimeSpan.Zero);
        await h.Coordinator.ObserveOnceAsync(default);
        second++;
    }
    for (; second <= 130; )
        await Tick(blocked: true);
    Check(h.Gateway.RecoverySendCalls == 0 && RecoveryRow(h.Db, "TASK-O13").State != ObstacleRecoveryStates.Frozen,
        "原链终态但无替代路线且仍受阻时，不发送也不冻结");
    for (; second <= 160 && h.Gateway.RecoverySendCalls == 0;)
        await Tick(blocked: false);
    Check(h.Gateway.RecoverySendCalls == 1 &&
          h.Gateway.SentPath.SequenceEqual(["S", "H", "X"]),
        "障碍清除且原链终态后，续发原剩余路线S→H→X");
}

static string? RecoveryState(ISqlSugarClient db, string taskId) =>
    db.Queryable<ObstacleRecoveryEntity>().ToList().FirstOrDefault(item => item.TaskId == taskId)?.State;

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
