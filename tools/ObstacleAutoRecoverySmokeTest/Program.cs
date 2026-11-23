using System.Text.Json;
using System.Buffers.Binary;
using AgvDispatch.Application.Abstractions;
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

var defaults = new ObstacleRecoveryOptions();
defaults.Validate();
var checks = 0;
void Check(bool valid, string label)
{
    if (!valid) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

Check(defaults.DetourAfterSeconds == 30 && defaults.CancelAfterSeconds == 120 &&
      defaults.WarningAfterSeconds == 60 && defaults.AlarmAfterSeconds == 120 &&
      defaults.ClosureSuggestionAfterSeconds == 300 && defaults.CancelTerminalWaitWarningSeconds == 30,
      "生产默认拆分30秒只读评估、120秒取消观察期和独立取消等待提醒");
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
        DetourAfterSeconds = 30,
        CancelAfterSeconds = 120,
        WarningAfterSeconds = 60,
        AlarmAfterSeconds = 120,
        ClosureSuggestionAfterSeconds = 300,
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
          gateway.RecoverySendCalls == 0 &&
          Decode(row.OriginalPathJson).SequenceEqual(["S", "H", "X"]),
          "障碍初期只观察，并使用发送时冻结的完整路线识别后续路段");

    clock.Advance(TimeSpan.FromSeconds(29));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(row.State == ObstacleRecoveryStates.Blocked &&
          gateway.CancelCalls == 0 && gateway.RecoverySendCalls == 0,
          "受阻未满30秒时保留车端恢复窗口，不提前取消任务或生成可发送改道");

    clock.Advance(TimeSpan.FromSeconds(2));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(row.State == ObstacleRecoveryStates.DetourReady &&
          Decode(row.CandidatePathJson).SequenceEqual(["S", "D", "X"]) &&
          row.StableStopSinceUtc is not null && row.StableStopKey == "S" &&
          gateway.CancelCalls == 0, "受阻30秒后只读生成避开S→H的安全候选，并持久化停稳观察起点");

    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 0 && row.StableStopSamples == 1,
          "重复读取同一遥测不得累计停稳样本或提前取消原导航");
    clock.Advance(TimeSpan.FromMilliseconds(100));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    Check(gateway.CancelCalls == 0,
          "连续样本数满足但实际停稳不足500毫秒时仍不得放行");

    clock.Advance(TimeSpan.FromMilliseconds(100));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true) with { VelocityX = 0.2 }, TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 0 && row.StableStopSamples == 0,
          "停稳观察途中移动会清除旧样本，不能沿用移动前的停稳时间");
    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    Check(gateway.CancelCalls == 0, "移动后重新停车必须重新累计停稳证据");

    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 0 && row.CancelRequestedAtUtc is null,
          "只读评估和连续停稳通过也不能在120秒观察期前取消");
    foreach (var second in new[] { 60, 119 })
    {
        clock.Advance(start.AddSeconds(second) - clock.GetUtcNow());
        states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
        await coordinator.ObserveOnceAsync(default);
        row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
        Check(gateway.CancelCalls == 0 && gateway.RecoverySendCalls == 0 &&
              row.CancelRequestedAtUtc is null, $"受阻{second}秒仍只读，不取消、不发送替代导航");
    }
    clock.Advance(start.AddSeconds(120) - clock.GetUtcNow());
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 1 && gateway.RecoverySendCalls == 0 &&
          row.State == ObstacleRecoveryStates.AwaitingOriginalTerminal,
          "连续停稳和安全条件通过后，只取消一次原3066任务");
    Check(gateway.StatusPaths.Count > 0 &&
          gateway.StatusPaths.All(path => path.SequenceEqual(["S", "H"])),
          "原3066取消核验只使用当前W窗口路径，不使用完整业务路线猜造分段编号");
    Check(row.CancelRequestedAtUtc == start.AddSeconds(120).UtcDateTime &&
          row.CancelAttemptedAtUtc is not null && row.CancelAcknowledgedAtUtc is not null &&
          row.OriginalTerminalAtUtc is null, "取消意图、网关调用及应答持久化；应答不伪造终态");
    gateway.OriginalState = AgvTransportTaskState.GoingToDestination;
    clock.Advance(TimeSpan.FromSeconds(31));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 1 && gateway.RecoverySendCalls == 0 &&
          row.OriginalTerminalAtUtc is null && row.NextAction!.Contains("超过30秒提醒线"),
          "取消应答后原链持续活动超过独立等待提醒，只提示、不重发、不续接");
    gateway.OriginalState = AgvTransportTaskState.Unknown;
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.CancelCalls == 1 && gateway.RecoverySendCalls == 0 && row.NextAction!.Contains("提醒线"),
          "原链未知仍保留取消等待提醒且不重发");
    gateway.OriginalState = AgvTransportTaskState.Cancelled;
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

    var restored = await new ObstacleRecoveryStore(db, recoveryOptions, clock).GetForVehicleAsync("A", default);
    Check(restored is { CancelRequestedAt: not null, CancelAttemptedAt: not null,
            CancelAcknowledgedAt: not null, OriginalTerminalAt: not null, CancelAfterSeconds: 120 } &&
          restored.OriginalTerminalAt > restored.CancelAcknowledgedAt,
          "恢复页重新读取保留取消到终态独立时间链和有效策略");
    Check(db.Queryable<ExecutionEvidenceEntity>().Count(item => item.TaskId == taskId &&
          (item.Kind == "ObstacleOriginalCancelIntent" || item.Kind == "ObstacleOriginalCancelAttempted" ||
           item.Kind == "ObstacleOriginalTerminalObserved")) == 3, "取消意图、调用和终态均有独立追加审计");
    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    row = db.Queryable<ObstacleRecoveryEntity>().Single(item => item.TaskId == taskId);
    Check(gateway.RecoverySendCalls == 0 &&
          row.State == ObstacleRecoveryStates.SafetyHold,
          "取消后的第一次安全复核只开始停稳计时，不立即发送");

    clock.Advance(TimeSpan.FromMilliseconds(100));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true) with
        { Tasks = [new SeerTaskSnapshot("EXTERNAL-ACTIVE", 2, null)] }, TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    Check(gateway.RecoverySendCalls == 0,
          "原链终态后发现其他活动厂家任务时不得并发发送替代导航");
    clock.Advance(TimeSpan.FromMilliseconds(100));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true) with
        { LocalizationConfidence = double.NaN }, TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    Check(gateway.RecoverySendCalls == 0,
          "定位置信度NaN不得绕过阈值校验或形成停稳证据");
    clock.Advance(TimeSpan.FromSeconds(1));
    states.ApplyTelemetry("A", Telemetry(clock.GetUtcNow(), blocked: true), TimeSpan.Zero);
    await coordinator.ObserveOnceAsync(default);
    Check(gateway.RecoverySendCalls == 0, "定位恢复后重新开始停稳观察，不沿用失效前证据");

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

foreach (var (initialVendorState, loseCancelResponse) in new[] {
    (AgvTransportTaskState.GoingToDestination, false),
    (AgvTransportTaskState.Failed, false),
    (AgvTransportTaskState.Completed, false),
    (AgvTransportTaskState.GoingToDestination, true) })
{
    var terminalBeforeCancellation = initialVendorState is AgvTransportTaskState.Failed or AgvTransportTaskState.Completed;
    var directory2 = Path.Combine(
        Path.GetTempPath(), "agv-safety-retreat-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory2);
    try
    {
        var start2 = new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);
        var clock2 = new ManualClock(start2);
        using var db2 = DatabaseFactory.Create(new()
        {
            DatabasePath = Path.Combine(directory2, "test.db")
        });
        var definition2 = new MapDefinition("MAP-R", "retreat", "RobotWorld", "m",
            [
                new("S", 0, 0, MapNodeType.Station, true, [], "S"),
                new("H", 1, 0, MapNodeType.Waypoint, true, [], "H"),
                new("BUS-X", 1, 1, MapNodeType.Station, true, [], "X")
            ],
            [
                new("E-SH", "S", "H", 1, 1, false, true, []),
                new("E-HX", "H", "BUS-X", 1, 1, false, true, [])
            ], []);
        var map2 = new ActiveMapRuntime();
        map2.Replace(new("MAP-R", "V1", definition2, start2));
        var catalog2 = new TrafficResourceCatalog(
            MapCorridorResources.AllResources(definition2));
        await new DatabaseInitializer(db2, catalog2).InitializeAsync(default);
        var gate2 = new MapExecutionGate();
        gate2.SetMode(MapExecutionMode.Running);
        var closures2 = new RoadClosureService(db2, map2, gate2);
        await closures2.InitializeAsync(default);
        var traffic2 = new TrafficResourceManager(
            db2, catalog2, new(TimeSpan.FromMinutes(1)), clock2);
        var agvOptions2 = new AgvOptions
        {
            Enabled = true,
            AllowCommands = true,
            StaleAfterMs = 10000,
            Robots =
            [
                new AgvRobotOptions
                {
                    VehicleId = "A",
                    IpAddress = "127.0.0.1",
                    Enabled = true
                }
            ]
        };
        var states2 = new AgvStateCache(agvOptions2, clock2);
        var tasks2 = new DispatchOrchestrationRepository(db2);
        var chains2 = new AgvTransportChainLookup(db2);
        var gateway2 = new RecoveryGateway();
        gateway2.OriginalState = initialVendorState;
        gateway2.LoseCancelResponse = loseCancelResponse;
        var expectedCancelCount = terminalBeforeCancellation ? 0 : 1;
        var recoveryOptions2 = new ObstacleRecoveryOptions
        {
            Enabled = true,
            AutomaticDetourEnabled = true,
            AutomaticSafetyPointRetreatEnabled = true,
            DetourAfterSeconds = 10,
        CancelAfterSeconds = 10,
            WarningAfterSeconds = 30,
            AlarmAfterSeconds = 60,
            ClosureSuggestionAfterSeconds = 120,
            StableStopObservationMs = 500,
            SafetyPointMaximumRetreatDistanceMeters = 5,
            SafetyPointRetreatMaximumSpeedMetersPerSecond = .1,
            SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared = .15
        };
        var store2 = new ObstacleRecoveryStore(db2, recoveryOptions2, clock2);
        var coordinator2 = new ObstacleRecoveryCoordinator(
            db2, states2, map2, tasks2, chains2, closures2, traffic2,
            new Envelope(), gate2, gateway2, new Preconditions(), store2,
            recoveryOptions2, new MapOptions { PositionStaleSeconds = 30 },
            new DispatchOrchestratorOptions
            {
                StandbySites = ["S"],
                DumperWaitingSite = "X",
                DumperDumpingSite = "X"
            },
            clock2, NullLogger<ObstacleRecoveryCoordinator>.Instance);

        const string taskId2 = "TASK-RETREAT";
        const string stepId2 = "STEP-RETREAT";
        await SeedTask(db2, taskId2, stepId2);
        states2.ApplyTelemetry(
            "A", Telemetry(clock2.GetUtcNow(), true, .4, 0, "S", "S", "H")
                with { Tasks = [new SeerTaskSnapshot(stepId2, 2, 1)] },
            TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        clock2.Advance(TimeSpan.FromSeconds(9));
        states2.ApplyTelemetry(
            "A", Telemetry(clock2.GetUtcNow(), true, .4, 0, "S", "S", "H")
                with { Tasks = [new SeerTaskSnapshot(stepId2, 2, 1)] },
            TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        var retreatRow2 = db2.Queryable<ObstacleRecoveryEntity>()
            .Single(item => item.TaskId == taskId2);
        Check(retreatRow2.State == ObstacleRecoveryStates.Blocked &&
              gateway2.CancelCalls == 0 &&
              gateway2.RetreatSendCalls == 0,
              "道路中段未满10秒时仅保留原任务并等待车端释放");

        clock2.Advance(TimeSpan.FromSeconds(2));
        states2.ApplyTelemetry(
            "A", Telemetry(clock2.GetUtcNow(), true, .4, 0, "S", "S", "H")
                with { Tasks = [new SeerTaskSnapshot(stepId2, 2, 1)] },
            TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        retreatRow2 = db2.Queryable<ObstacleRecoveryEntity>()
            .Single(item => item.TaskId == taskId2);
        Check(retreatRow2.State == ObstacleRecoveryStates.SafetyPointReady &&
              retreatRow2.SafetyPointTarget == "S" &&
              gateway2.CancelCalls == 0,
              "道路中段受阻10秒后选择后方安全站，尚未越过停稳门槛发送");

        clock2.Advance(TimeSpan.FromMilliseconds(100));
        states2.ApplyTelemetry("A",
            Telemetry(clock2.GetUtcNow(), true, .4, 0, "S", "S", "H") with { Angle = Math.PI }, TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        Check(gateway2.CancelCalls == 0 && gateway2.RetreatSendCalls == 0,
              "车头反向时禁止把3055负速度当成沿原边后退");
        clock2.Advance(TimeSpan.FromMilliseconds(100));
        states2.ApplyTelemetry("A",
            Telemetry(clock2.GetUtcNow(), true, .4, .08, "S", "S", "H"), TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        Check(gateway2.CancelCalls == 0 && gateway2.RetreatSendCalls == 0,
              "短退横向偏差过大时不得发送直线平动后退");
        clock2.Advance(TimeSpan.FromMilliseconds(100));
        states2.ApplyTelemetry("A",
            Telemetry(clock2.GetUtcNow(), true, .4, 0, "S", "S", "H"), TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);

        clock2.Advance(TimeSpan.FromSeconds(1));
        states2.ApplyTelemetry(
            "A", Telemetry(clock2.GetUtcNow(), true, .4, 0, "S", "S", "H")
                with { Tasks = [new SeerTaskSnapshot(stepId2, 2, 1)] },
            TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        retreatRow2 = db2.Queryable<ObstacleRecoveryEntity>()
            .Single(item => item.TaskId == taskId2);
        Check(gateway2.CancelCalls == expectedCancelCount &&
              retreatRow2.State == (loseCancelResponse ? ObstacleRecoveryStates.Uncertain : ObstacleRecoveryStates.AwaitingOriginalTerminal),
              $"安全点退回前只按原3066任务编号取消一次: {gateway2.CancelCalls}, {retreatRow2.State}, {retreatRow2.Message}");

        Check(db2.Queryable<ExecutionEvidenceEntity>().Count(item => item.TaskId == taskId2 &&
              item.Kind == "ObstacleSafetyPointRetreatPlanned") == 1,
              "同一退站候选持续观察时只记一次规划事件，避免时间轴重复刷屏");
        if (terminalBeforeCancellation)
            Check(retreatRow2.CancelRequestedAtUtc is null && retreatRow2.CancelAttemptedAtUtc is null &&
                  retreatRow2.CancelAcknowledgedAtUtc is null && retreatRow2.OriginalTerminalAtUtc is not null,
                  $"原窗口自然{initialVendorState}只记录终态，不伪造取消时间");
        if (loseCancelResponse)
            Check(retreatRow2.CancelRequestedAtUtc is not null && retreatRow2.CancelAttemptedAtUtc is not null &&
                  retreatRow2.CancelAcknowledgedAtUtc is null && gateway2.RetreatSendCalls == 0,
                  "取消响应丢失保存调用时间，保持结果未知且不提前退站");

        for (var index = 0; index < 4; index++)
        {
            clock2.Advance(TimeSpan.FromSeconds(1));
            states2.ApplyTelemetry(
                "A", Telemetry(clock2.GetUtcNow(), true, .4, 0, "S"),
                TimeSpan.Zero);
            await coordinator2.ObserveOnceAsync(default);
        }
        retreatRow2 = db2.Queryable<ObstacleRecoveryEntity>()
            .Single(item => item.TaskId == taskId2);
        Check(gateway2.RetreatSendCalls == 1 &&
              gateway2.RetreatTarget == "S" &&
              Math.Abs(gateway2.RetreatSpeed - .1) < 1e-9 &&
              Math.Abs(gateway2.RetreatDistance - .4) < 1e-6 &&
              retreatRow2.SafetyPointRetreatMethod == "3055-Translation" &&
              retreatRow2.State == ObstacleRecoveryStates.SafetyPointMoving,
              "同一短直边、原链终态和连续停稳后只发送一次3055平动后退");

        gateway2.RetreatState = AgvTransportTaskState.Completed;
        clock2.Advance(TimeSpan.FromSeconds(1));
        states2.ApplyTelemetry(
            "A", Telemetry(clock2.GetUtcNow(), false, 0, 0, "S"),
            TimeSpan.Zero);
        await coordinator2.ObserveOnceAsync(default);
        retreatRow2 = db2.Queryable<ObstacleRecoveryEntity>()
            .Single(item => item.TaskId == taskId2);
        Check(retreatRow2.State == ObstacleRecoveryStates.SafetyPointReached &&
              retreatRow2.SafetyPointReachedAtUtc is not null,
              "车辆到后方安全站并停稳后才核定为已退站");

        for (var index = 0; index < 2; index++)
        {
            clock2.Advance(TimeSpan.FromSeconds(1));
            states2.ApplyTelemetry(
                "A", Telemetry(clock2.GetUtcNow(), false, 0, 0, "S"),
                TimeSpan.Zero);
            await coordinator2.ObserveOnceAsync(default);
        }
        retreatRow2 = db2.Queryable<ObstacleRecoveryEntity>()
            .Single(item => item.TaskId == taskId2);
        Check(gateway2.CancelCalls == expectedCancelCount &&
              gateway2.RetreatSendCalls == 1 &&
              gateway2.RecoverySendCalls == 1 &&
              retreatRow2.State == ObstacleRecoveryStates.Executing,
              "退站完成后由原车发送一条替代3066，不重发取消或3055");
        Check(db2.Queryable<ExecutionEvidenceEntity>().Any(item =>
                  item.TaskId == taskId2 &&
                  item.Kind == "ObstacleSafetyPointRetreatSent") &&
              db2.Queryable<ExecutionEvidenceEntity>().Any(item =>
                  item.TaskId == taskId2 &&
                  item.Kind == "ObstacleSafetyPointReached"),
              "3055退站、到站和后续替代路线均留有审计证据");
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Path.GetFullPath(directory2).StartsWith(
                Path.GetFullPath(Path.GetTempPath()),
                StringComparison.OrdinalIgnoreCase))
            Directory.Delete(directory2, true);
    }
}

{
    var listener = new System.Net.Sockets.TcpListener(
        System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var capture = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        using var stream = tcp.GetStream();
        var requests = new List<(int Type, JsonElement Payload)>();
        for (var index = 0; index < 2; index++)
        {
            var header = new byte[16];
            await stream.ReadExactlyAsync(header);
            var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4))];
            await stream.ReadExactlyAsync(payload);
            var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
            using var request = JsonDocument.Parse(payload);
            requests.Add((type, request.RootElement.Clone()));
            var response = JsonSerializer.SerializeToUtf8Bytes(new { ret_code = 0 });
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), response.Length);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)(type + 10000));
            await stream.WriteAsync(header);
            await stream.WriteAsync(response);
        }
        return requests;
    });
    var protocolOptions = new AgvOptions
    {
        Enabled = true,
        AllowCommands = true,
        Robots =
        [
            new AgvRobotOptions
            {
                VehicleId = "A",
                IpAddress = "127.0.0.1",
                Port = port,
                NavigationPort = port,
                Enabled = true
            }
        ]
    };
    await using var protocolClient = new SeerRobotClient(
        protocolOptions.Robots[0], protocolOptions);
    await protocolClient.TranslateBackwardAsync(.75, .1, default);
    await protocolClient.GoFromCurrentPoseBackwardAsync(
        "SAFETY-3051", "S", .1, .15, default);
    var sent = await capture;
    listener.Stop();
    Check(sent[0].Type == 3055 &&
          sent[0].Payload.GetProperty("dist").GetDouble() == .75 &&
          sent[0].Payload.GetProperty("vx").GetDouble() == -.1 &&
          sent[0].Payload.GetProperty("vy").GetDouble() == 0 &&
          sent[0].Payload.GetProperty("mode").GetInt32() == 1,
          "一米内短直边使用3055直线后退，不要求车端重新规划整圈路线");
    Check(sent[1].Type == 3051 &&
          sent[1].Payload.GetProperty("source_id").GetString() == "SELF_POSITION" &&
          sent[1].Payload.GetProperty("id").GetString() == "S" &&
          sent[1].Payload.GetProperty("task_id").GetString() == "SAFETY-3051" &&
          sent[1].Payload.GetProperty("method").GetString() == "backward" &&
          sent[1].Payload.GetProperty("max_speed").GetDouble() == .1 &&
          sent[1].Payload.GetProperty("max_acc").GetDouble() == .15,
          "3051实际报文只携带当前位置、后方站、唯一任务号、倒走和受限速度");
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
    // The current rolling window only covers S→H. The immutable send snapshot
    // retains the complete S→H→X route and is the recovery source of truth.
    var payload = new AgvTransportCommandPayload(stepId, "A", "S", "H", ["S", "H"], null);
    var payloadJson = JsonSerializer.Serialize(payload,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var snapshotJson = JsonSerializer.Serialize(new TransportRouteSnapshot(
        1, taskId, stepId, "CMD-1", "A", "MAP-O", "V1", "RobotWorld", "DEFINITION",
        TransportRouteSnapshots.Hash(payloadJson), ["S", "H", "X"], ["E-SH", "E-HX"],
        ["S", "H", "X"]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await db.Insertable(new AgvCommandOutboxEntity
    {
        CommandId = "CMD-1",
        IdempotencyKey = "TEST:" + stepId,
        TaskId = taskId,
        StepId = stepId,
        VehicleId = "A",
        CommandType = (int)AgvCommandType.Transport,
        Status = (int)AgvCommandStatus.Sent,
        PayloadJson = payloadJson,
        ExecutionMapId = "MAP-O",
        ExecutionMapVersionId = "V1",
        ExecutionRouteSnapshotJson = snapshotJson,
        ExecutionRouteSnapshotHash = TransportRouteSnapshots.Hash(snapshotJson),
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
        SentAt = now,
        VendorCommandId = stepId
    }).ExecuteCommandAsync();
}

static string[] Decode(string json) =>
    JsonSerializer.Deserialize<string[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

static AgvRobotTelemetry Telemetry(
    DateTimeOffset at,
    bool blocked,
    double x = 0,
    double y = 0,
    string current = "S",
    string? source = null,
    string? target = null) =>
    new(x, y, 0, .99, current, null, 80, blocked, false, "Running",
        source, target, null, [], [], at)
    {
        VelocityX = 0,
        VelocityY = 0,
        AngularVelocity = 0,
        Charging = false
    };

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset current = start;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan span) => current += span;
}

sealed class RecoveryGateway : IAgvGateway, IAgvObstacleRecoveryGateway,
    IAgvSafetyPointRecoveryGateway
{
    public bool LoseCancelResponse { get; set; }
    public int CancelCalls { get; private set; }
    public int RecoverySendCalls { get; private set; }
    public string? NewVendorTaskId { get; private set; }
    public IReadOnlyList<string> SentPath { get; private set; } = [];
    public List<string[]> StatusPaths { get; } = [];
    public int RetreatSendCalls { get; private set; }
    public string? RetreatTaskId { get; private set; }
    public string? RetreatTarget { get; private set; }
    public double RetreatSpeed { get; private set; }
    public double RetreatDistance { get; private set; }
    public AgvTransportTaskState RetreatState { get; set; } = AgvTransportTaskState.GoingToDestination;
    private AgvTransportTaskState original = AgvTransportTaskState.GoingToDestination;
    public AgvTransportTaskState OriginalState { set => original = value; }
    public Task<string> CreateTransportTaskAsync(string businessTaskId, string? preferredVehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("通用发送口在受阻状态必须被拒绝。");
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId,
        CancellationToken cancellationToken) =>
        string.Equals(vendorTaskId, RetreatTaskId, StringComparison.Ordinal)
            ? Task.FromResult<AgvTransportTaskStatus?>(new(
                vendorTaskId, vendorTaskId, RetreatState, "A",
                DateTimeOffset.UtcNow, "synthetic retreat"))
            : Status(vendorTaskId);
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId, string vehicleId,
        IReadOnlyList<string> originalPath, CancellationToken cancellationToken)
    {
        StatusPaths.Add(originalPath.ToArray());
        return Status(vendorTaskId);
    }
    public Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken)
    {
        CancelCalls++;
        original = AgvTransportTaskState.Cancelled;
        if (LoseCancelResponse) throw new TimeoutException("synthetic lost cancellation response");
        return Task.CompletedTask;
    }
    public Task<string> CreateObstacleRecoveryTransportTaskAsync(string newVendorTaskId, string vehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, DateTimeOffset blockedSince,
        string originalVendorTaskId, ObstacleRecoveryPreSendLimits limits, CancellationToken cancellationToken)
    {
        RecoverySendCalls++;
        NewVendorTaskId = newVendorTaskId;
        SentPath = path.ToArray();
        return Task.FromResult(newVendorTaskId);
    }
    public Task<string> CreateObstacleSafetyPointRetreatAsync(string retreatVendorTaskId,
        string vehicleId, string targetSite, string blockedSourceSite,
        string blockedTargetSite, double maximumSpeedMetersPerSecond,
        double maximumAccelerationMetersPerSecondSquared, double remainingDistanceMeters,
        DateTimeOffset blockedSince,
        string originalVendorTaskId, ObstacleRecoveryPreSendLimits limits, CancellationToken cancellationToken)
    {
        RetreatSendCalls++;
        RetreatTaskId = retreatVendorTaskId;
        RetreatTarget = targetSite;
        RetreatSpeed = maximumSpeedMetersPerSecond;
        RetreatDistance = remainingDistanceMeters;
        RetreatState = AgvTransportTaskState.GoingToDestination;
        return Task.FromResult(retreatVendorTaskId);
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
