using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

// Entirely isolated: every fixture has its own temporary SQLite database and a synthetic port.
// No service configuration, network endpoint, robot connection or live data directory is read.
var tests = new (string Name, Func<Task> Run)[]
{
    ("cancel-close-create-handoff", FullChain),
    ("arrival-state-mode-policy", ArrivalModePolicy),
    ("loaded-return-create-without-cancel", LoadedReturn),
    ("loaded-return-restart-no-side-effect", LoadedReturnRestart),
    ("suspend-resume-preserves-original", SuspendResume),
    ("cleanup-blockers-hold-vehicle", CleanupBlockers),
    ("multi-vehicle-insertion-isolation", MultiVehicleIsolation),
    ("five-vehicle-switch-reservations", FiveVehicleReservations),
    ("unknown-original-and-action-block", UnknownOriginal),
    ("lost-cancel-response-stable-request", LostCancelResponse),
    ("restart-holds-until-explicit-resume", Restart),
    ("idempotent-request-and-parameter-conflict", Replays),
    ("concurrent-vehicle-and-source-reservations", ConcurrentReservation),
    ("lost-create-response-queries-fixed-receipt", LostCreateResponse),
    ("missing-create-receipt-retains-lock", MissingReceipt),
    ("map-version-change-keeps-original-pin", VersionChange),
    ("abandon-requires-closed-original", Abandon),
    ("identity-and-revision-database-guard", DatabaseGuard),
    ("stale-resume-revision-rejected", StaleResume),
    ("legacy-schema-mode-migration", LegacySchemaMigration),
    ("handoff-commissioning-contract", HandoffContract)
};
foreach (var test in tests) { await test.Run(); Console.WriteLine("PASS " + test.Name); }
Console.WriteLine($"PASS TaskSwitchSmokeTest: {tests.Length} isolated scenarios; no robot actions.");

static async Task FullChain()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", null, null, default), "active switch reserves vehicle");
    Require(await f.Guard.AllowsVehicleAsync("CAR-X", null, current.ReplacementRequestId, default), "fixed replacement request may create");
    await f.Service.ProcessAsync(default);
    current = await f.Current(current.SwitchId);
    Require(current.Stage == TaskSwitchStages.AwaitingStop && f.Port.CancelCalls == 1, "cancel accepted is not stopped");
    await f.Service.ProcessAsync(default);
    Require((await f.Current(current.SwitchId)).Stage == TaskSwitchStages.AwaitingStop, "wait for original terminal");
    f.Port.CancelConfirmed = true;
    f.Port.CanClose = true;
    await f.Service.ProcessAsync(default);
    await f.Service.ProcessAsync(default);
    Require(f.Port.CloseCalls == 1 && f.Port.OriginalClosed, "explicit close action only after proof");
    await f.PumpUntil(current.SwitchId, TaskSwitchStages.Completed);
    current = await f.Current(current.SwitchId);
    Require(current.TargetTaskId is not null && f.Port.CreateCalls == 1, "new task receipt saved");
    Require(f.Port.CancelKeys.Distinct().Count() == 1 && f.Port.CreateKeys.Single() == current.ReplacementRequestId, "stable IDs");
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", "OTHER", null, default), "completed switch retains pending handoff lock");
    Require(await f.Guard.AllowsVehicleAsync("CAR-X", current.TargetTaskId, null, default), "target task alone may dispatch");
    var targetStep = await f.Db.Queryable<DispatchTaskStepEntity>().SingleAsync(s => s.TaskId == current.TargetTaskId);
    Require(targetStep.StepId != "OLD-STEP", "replacement never reuses original vendor/step key");
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { Stage = (int)DispatchTaskStage.SubmittingToAgv })
        .Where(t => t.TaskId == current.TargetTaskId).ExecuteCommandAsync();
    Require(await f.Guard.AllowsVehicleAsync("CAR-X", "OTHER", null, default), "normal assigned task ownership takes over handoff guard");
}

static Task ArrivalModePolicy()
{
    Require(TaskSwitchModes.Resolve(DispatchTaskStage.GoingToPickup, true, false) == TaskSwitchModes.CancelEmptyNavigation,
        "empty lowered arrival cancels navigation");
    Require(TaskSwitchModes.Resolve(DispatchTaskStage.GoingToPickup, false, true) == TaskSwitchModes.CompleteBusinessReturn,
        "loaded raised arrival continues business cleanup");
    Require(TaskSwitchModes.Resolve(DispatchTaskStage.GoingToPickup, false, false) == "",
        "unknown arrival state is not guessed");
    Require(TaskSwitchModes.Resolve(DispatchTaskStage.Dumping, false, false) == TaskSwitchModes.CompleteBusinessReturn,
        "post-pickup business stage always completes original");
    return Task.CompletedTask;
}

static async Task LoadedReturn()
{
    using var f = await Fixture.Create();
    f.Port.InspectionMode = TaskSwitchModes.CompleteBusinessReturn;
    var request = f.Request() with { ExpectedStage = DispatchTaskStage.PickingUp, Mode = TaskSwitchModes.CompleteBusinessReturn };
    var started = await f.Service.RequestAsync(f.User, request, default);
    Require(started.Accepted && started.Current!.ResolvedMode == TaskSwitchModes.CompleteBusinessReturn, "loaded task selects return cleanup");
    var current = started.Current!;
    await f.Service.ProcessAsync(default);
    current = await f.Current(current.SwitchId);
    Require(current.Stage == TaskSwitchStages.AwaitingBusinessCleanup && f.Port.CancelCalls == 0, "loaded task is never cancelled");
    await f.Service.ProcessAsync(default);
    Require((await f.Current(current.SwitchId)).Stage == TaskSwitchStages.AwaitingBusinessCleanup && f.Port.CreateCalls == 0, "in-progress business remains locked");
    f.Port.CleanupConfirmed = f.Port.OriginalClosed = true;
    await f.PumpUntil(current.SwitchId, TaskSwitchStages.Completed);
    Require(f.Port.CancelCalls == 0 && f.Port.CloseCalls == 0 && f.Port.CreateCalls == 1, "only terminal business completion creates replacement");
}

static async Task LoadedReturnRestart()
{
    using var f = await Fixture.Create();
    f.Port.InspectionMode = TaskSwitchModes.CompleteBusinessReturn;
    var request = f.Request() with { ExpectedStage = DispatchTaskStage.ReturningEmptyBox, Mode = TaskSwitchModes.CompleteBusinessReturn };
    var started = await f.Service.RequestAsync(f.User, request, default);
    Require(started.Accepted, "returning task accepted for cleanup");
    await f.Service.ProcessAsync(default);
    var current = await f.Current(started.Current!.SwitchId);
    var restarted = new TaskSwitchService(f.Db, f.Port);
    await restarted.InitializeAsync(default);
    current = (await restarted.GetAsync(current.SwitchId, default))!;
    Require(current.Stage == TaskSwitchStages.ReviewHold && current.ResumeStage == TaskSwitchStages.AwaitingBusinessCleanup, "restart holds cleanup phase");
    Require(f.Port.CancelCalls == 0 && f.Port.CreateCalls == 0, "restart sends no cancel or replacement");
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", null, null, default), "restart keeps vehicle locked");
}

static async Task SuspendResume()
{
    using var f = await Fixture.Create();
    await f.Db.Insertable(new DispatchTaskEntity
    {
        TaskId = "SOURCE-1", StationId = "CNC-001", VehicleId = "CAR-X", Stage = (int)DispatchTaskStage.PickingUp,
        CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
    }).ExecuteCommandAsync();
    f.Port.InspectionMode = TaskSwitchModes.CompleteBusinessReturn;
    var request = f.Request() with { ExpectedStage = DispatchTaskStage.PickingUp, Mode = TaskSwitchModes.CompleteBusinessReturn };
    var started = await f.Service.RequestAsync(f.User, request, default);
    Require(started.Accepted, "cleanup request accepted before suspension");
    await f.Service.ProcessAsync(default);
    var current = await f.Current(started.Current!.SwitchId);
    var suspended = await f.Service.SuspendAsync(f.User, Review(current), default);
    Require(suspended.Accepted && suspended.Current!.Stage == TaskSwitchStages.Suspended &&
        suspended.Current.ResumeStage == TaskSwitchStages.AwaitingBusinessCleanup,
        "switch suspends at cleanup phase");
    await f.Service.ProcessAsync(default);
    Require((await f.Current(current.SwitchId)).Stage == TaskSwitchStages.Suspended &&
        f.Port.CancelCalls == 0 && f.Port.CreateCalls == 0 &&
        !await f.Guard.AllowsVehicleAsync("CAR-X", "OTHER", null, default),
        "suspended switch retains original vehicle and creates nothing");
    current = await f.Current(current.SwitchId);
    var resumed = await f.Service.ResumeAsync(f.User, Review(current), default);
    Require(resumed.Accepted && resumed.Current!.Stage == TaskSwitchStages.AwaitingBusinessCleanup,
        "explicit resume restores original cleanup phase");
    var source = await f.Db.Queryable<DispatchTaskEntity>().SingleAsync(t => t.TaskId == "SOURCE-1");
    Require(source.Stage == (int)DispatchTaskStage.PickingUp, "suspension never rewrites original task");
}

static async Task CleanupBlockers()
{
    using var f = await Fixture.Create();
    f.Port.InspectionMode = TaskSwitchModes.CompleteBusinessReturn;
    var request = f.Request() with { ExpectedStage = DispatchTaskStage.ReturningEmptyBox, Mode = TaskSwitchModes.CompleteBusinessReturn };
    var started = await f.Service.RequestAsync(f.User, request, default);
    Require(started.Accepted, "cleanup request accepted");
    await f.Service.ProcessAsync(default);
    f.Port.ObservationBlockers = ["原PLC握手或车辆状态尚未闭合"];
    await f.Service.ProcessAsync(default);
    var held = await f.Current(started.Current!.SwitchId);
    Require(held.Stage == TaskSwitchStages.ReviewHold && held.Blockers.Contains(f.Port.ObservationBlockers[0]),
        "cleanup blocker freezes the switch");
    Require(f.Port.CancelCalls == 0 && f.Port.CreateCalls == 0 && !await f.Guard.AllowsVehicleAsync("CAR-X", null, null, default),
        "blocked cleanup keeps ownership and sends nothing");
}

static async Task MultiVehicleIsolation()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", "OTHER-X", null, default), "original vehicle remains reserved");
    Require(await f.Guard.AllowsVehicleAsync("CAR-Y", "OTHER-Y", null, default), "other vehicle may accept an inserted task");
    var conflict = await f.Service.RequestAsync(f.User, f.Request("SOURCE-2"), default);
    Require(!conflict.Accepted && conflict.Current!.SwitchId == current.SwitchId, "same vehicle cannot acquire a second switch");
}

static async Task FiveVehicleReservations()
{
    using var f = await Fixture.Create();
    for (var index = 1; index <= 5; index++)
    {
        f.Port.VehicleId = $"CAR-{index}";
        f.Port.InspectionMode = TaskSwitchModes.CancelEmptyNavigation;
        var result = await f.Service.RequestAsync(f.User, f.Request($"SOURCE-{index}"), default);
        Require(result.Accepted, $"vehicle {index} acquires one switch reservation");
    }
    Require((await f.Service.ListAsync(default)).Count == 5, "five vehicles retain five independent switch sessions");
    for (var index = 1; index <= 5; index++)
        Require(!await f.Guard.AllowsVehicleAsync($"CAR-{index}", "OTHER", null, default),
            $"CAR-{index} original responsibility remains reserved");
    Require(await f.Guard.AllowsVehicleAsync("CAR-6", "INSERTED", null, default),
        "unreserved sixth vehicle accepts an inserted task");
}

static async Task UnknownOriginal()
{
    using var f = await Fixture.Create();
    f.Port.InspectionBlockers = ["首段包含取箱动作JackLoad，不能推断空载"];
    var rejected = await f.Service.RequestAsync(f.User, f.Request(), default);
    Require(!rejected.Accepted && f.Port.CancelCalls == 0, "inspection refuses action-bearing pickup");
    f.Port.InspectionBlockers = [];
    var current = await f.Start();
    f.Port.ObservationBlockers = ["原运输状态未知，不能确认取消范围"];
    await f.Service.ProcessAsync(default);
    current = await f.Current(current.SwitchId);
    Require(current.Stage == TaskSwitchStages.ReviewHold && current.Blockers.Contains(f.Port.ObservationBlockers[0]), "concrete blocker persisted");
    Require(f.Port.CancelCalls == 0 && f.Port.CreateCalls == 0, "unknown original does not cancel/create");
}

static async Task LostCancelResponse()
{
    using var f = await Fixture.Create();
    f.Port.ThrowCancelOnce = true;
    var current = await f.Start();
    await f.Service.ProcessAsync(default);
    current = await f.Current(current.SwitchId);
    Require(current.Stage == TaskSwitchStages.ReviewHold && current.ResumeStage == TaskSwitchStages.Requested, "lost cancel freezes at durable intent");
    var replay = await f.Service.RequestAsync(f.User, current.Request, default);
    Require(replay.Accepted && f.Port.CancelCalls == 1, "replayed request is read-only");
    Require((await f.Service.ResumeAsync(f.User, Review(current), default)).Accepted, "explicit resume");
    await f.Service.ProcessAsync(default);
    Require(f.Port.CancelCalls == 2 && f.Port.CancelKeys.Distinct().Count() == 1, "same cancellation identity reused");
    f.Port.CancelConfirmed = f.Port.OriginalClosed = true;
    await f.PumpUntil(current.SwitchId, TaskSwitchStages.Completed);
    Require(f.Port.CreateCalls == 1, "one replacement");
}

static async Task Restart()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    await f.Service.ProcessAsync(default);
    var restarted = new TaskSwitchService(f.Db, f.Port);
    await restarted.InitializeAsync(default);
    current = (await restarted.GetAsync(current.SwitchId, default))!;
    Require(current.Stage == TaskSwitchStages.ReviewHold && current.ResumeStage == TaskSwitchStages.AwaitingStop, "restart holds original phase");
    await restarted.ProcessAsync(default);
    Require(f.Port.CancelCalls == 1 && f.Port.CreateCalls == 0, "restart does not repeat side effect");
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", null, null, default), "restart reservation survives");
    Require((await restarted.ResumeAsync(f.User, Review(current), default)).Accepted, "restart requires explicit resume");
    f.Port.CancelConfirmed = true;
    await restarted.ProcessAsync(default);
    Require((await restarted.GetAsync(current.SwitchId, default))!.Stage == TaskSwitchStages.ClosingOriginal, "continues original stop observation");
}

static async Task Replays()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    var originalCancel = current.CancelRequestId;
    var replay = await f.Service.RequestAsync(f.User, current.Request, default);
    Require(replay.Accepted && replay.Current!.Revision == current.Revision && replay.Current.CancelRequestId == originalCancel, "replay no new revisions or IDs");
    var conflict = await f.Service.RequestAsync(f.User, current.Request with { TargetStationId = "STATION-Z" }, default);
    Require(!conflict.Accepted && conflict.Current!.SwitchId == current.SwitchId, "different parameters conflict");
    Require((await f.Service.ListAsync(default)).Count == 1, "one persisted request");
}

static async Task ConcurrentReservation()
{
    using var f = await Fixture.Create();
    var results = await Task.WhenAll(f.Service.RequestAsync(f.User, f.Request(), default), f.Service.RequestAsync(f.User, f.Request("SOURCE-2"), default));
    Require(results.Count(r => r.Accepted) == 1, "one vehicle one active session");
    var winner = results.Single(r => r.Accepted).Current!;
    var row = await f.Db.Queryable<TaskSwitchEntity>().SingleAsync(r => r.SwitchId == winner.SwitchId);
    row.SwitchId = Guid.NewGuid().ToString("N"); row.ReplacementRequestId = Guid.NewGuid().ToString("N"); row.CancelRequestId = Guid.NewGuid().ToString("N");
    row.SourceTaskId = "SOURCE-DIFFERENT";
    var failed = false;
    try { await f.Db.Insertable(row).ExecuteCommandAsync(); } catch { failed = true; }
    Require(failed, "database unique index independently reserves vehicle");
    row.VehicleId = "CAR-Y"; row.SourceTaskId = winner.SourceTaskId;
    failed = false;
    try { await f.Db.Insertable(row).ExecuteCommandAsync(); } catch { failed = true; }
    Require(failed, "database unique index independently reserves source task");
}

static async Task LostCreateResponse()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    f.Port.CancelConfirmed = f.Port.OriginalClosed = true;
    f.Port.ThrowCreateOnce = true;
    await f.PumpUntil(current.SwitchId, TaskSwitchStages.ReviewHold);
    current = await f.Current(current.SwitchId);
    Require(current.ReplacementCreateAttempted && f.Port.CreateCalls == 1, "create intent durable before lost response");
    var receipt = await f.Db.Queryable<DispatchTaskEntity>().SingleAsync(t => t.CreateRequestId == current.ReplacementRequestId);
    Require(await f.Guard.AllowsVehicleAsync("CAR-X", receipt.TaskId, null, default), "committed receipt can dispatch before coordinator saves target ID");
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", "OTHER", null, default), "other tasks still locked");
    var restarted = new TaskSwitchService(f.Db, f.Port);
    await restarted.InitializeAsync(default);
    current = (await restarted.GetAsync(current.SwitchId, default))!;
    Require((await restarted.ResumeAsync(f.User, Review(current), default)).Accepted, "explicit resume after lost creation");
    await restarted.ProcessAsync(default);
    current = (await restarted.GetAsync(current.SwitchId, default))!;
    Require(current.Stage == TaskSwitchStages.Completed && current.TargetTaskId == receipt.TaskId && f.Port.CreateCalls == 1, "recover by same receipt without new create");
}

static async Task MissingReceipt()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    f.Port.CancelConfirmed = f.Port.OriginalClosed = true;
    f.Port.ThrowCreateOnce = true; f.Port.PersistCreate = false;
    await f.PumpUntil(current.SwitchId, TaskSwitchStages.ReviewHold);
    for (var i = 0; i < 2; i++)
    {
        current = await f.Current(current.SwitchId);
        Require((await f.Service.ResumeAsync(f.User, Review(current), default)).Accepted, "resume permits receipt reconciliation only");
        await f.Service.ProcessAsync(default);
    }
    current = await f.Current(current.SwitchId);
    Require(current.Stage == TaskSwitchStages.ReviewHold && f.Port.CreateCalls == 1, "missing receipt cannot recreate");
    Require(!(await f.Service.AbandonAsync(f.User, Review(current), default)).Accepted, "uncertain creation cannot abandon");
    Require(!await f.Guard.AllowsVehicleAsync("CAR-X", null, null, default), "uncertain creation retains vehicle");
}

static async Task VersionChange()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    f.Port.CancelConfirmed = f.Port.OriginalClosed = true;
    await f.PumpUntil(current.SwitchId, TaskSwitchStages.ReadyToSwitch);
    f.Port.Version = "MAP-V2";
    await f.Service.ProcessAsync(default);
    current = await f.Current(current.SwitchId);
    Require(current.Stage == TaskSwitchStages.ReviewHold && f.Port.CreateCalls == 0 && current.MapVersionId == "MAP-V1", "map change keeps original pin and prevents create");
    Require(!(await f.Service.ResumeAsync(f.User, Review(current), default)).Accepted, "resume cannot silently bind new map");
}

static async Task Abandon()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    Require(!(await f.Service.AbandonAsync(f.User, Review(current), default)).Accepted, "cannot abandon open source");
    current = await f.Current(current.SwitchId);
    f.Port.CancelConfirmed = f.Port.OriginalClosed = true;
    var result = await f.Service.AbandonAsync(f.User, Review(current), default);
    Require(result.Accepted && result.Current!.Stage == TaskSwitchStages.Abandoned && f.Port.CreateCalls == 0, "abandon only after original closure and before create");
    Require(await f.Guard.AllowsVehicleAsync("CAR-X", null, null, default), "abandoned reservation released");
}

static async Task DatabaseGuard()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    var failed = false;
    try { await f.Db.Updateable<TaskSwitchEntity>().SetColumns(r => new TaskSwitchEntity { ReplacementRequestId = "tampered", Revision = current.Revision + 1 }).Where(r => r.SwitchId == current.SwitchId).ExecuteCommandAsync(); }
    catch { failed = true; }
    Require(failed, "request identity immutable");
    failed = false;
    try { await f.Db.Updateable<TaskSwitchEntity>().SetColumns(r => new TaskSwitchEntity { Stage = TaskSwitchStages.Completed }).Where(r => r.SwitchId == current.SwitchId).ExecuteCommandAsync(); }
    catch { failed = true; }
    Require(failed, "revision must advance exactly once");
    failed = false;
    try { await f.Db.Deleteable<TaskSwitchEntity>().Where(r => r.SwitchId == current.SwitchId).ExecuteCommandAsync(); }
    catch { failed = true; }
    Require(failed, "audit session cannot be deleted");
}

static async Task StaleResume()
{
    using var f = await Fixture.Create();
    var current = await f.Start();
    f.Port.ObservationBlockers = ["原PLC周期未闭合"];
    await f.Service.ProcessAsync(default);
    var held = await f.Current(current.SwitchId);
    f.Port.ObservationBlockers = [];
    Require(!(await f.Service.ResumeAsync(f.User, Review(current), default)).Accepted, "old revision rejected");
    Require((await f.Current(current.SwitchId)).Revision == held.Revision, "stale request does not mutate record");
    Require(!(await f.Service.ResumeAsync(f.User, Review(held) with { SiteConfirmed = false }, default)).Accepted, "site confirmation mandatory");
}

static async Task LegacySchemaMigration()
{
    var path = Path.Combine(Path.GetTempPath(), "TaskSwitchMigration-" + Guid.NewGuid().ToString("N") + ".db");
    using var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = path });
    await db.Ado.ExecuteCommandAsync("""
        CREATE TABLE task_switch_session (
          SwitchId TEXT PRIMARY KEY, SourceTaskId TEXT NOT NULL, VehicleId TEXT NOT NULL, TargetStationId TEXT NOT NULL,
          TargetTaskId TEXT NULL, Stage TEXT NOT NULL, ResumeStage TEXT NULL, Message TEXT NOT NULL, BlockersJson TEXT NOT NULL,
          Revision INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL, MapId TEXT NOT NULL, MapVersionId TEXT NOT NULL,
          ParameterRevision TEXT NOT NULL, ReplacementRequestId TEXT NOT NULL, CancelRequestId TEXT NOT NULL,
          RequestJson TEXT NOT NULL, Operator TEXT NOT NULL, ReplacementCreateAttempted INTEGER NOT NULL
        );
        CREATE TRIGGER guard_task_switch_identity BEFORE UPDATE ON task_switch_session
        WHEN NEW.SwitchId IS NOT OLD.SwitchId OR NEW.Revision != OLD.Revision + 1
        BEGIN SELECT RAISE(ABORT,'legacy guard'); END;
        """);
    var id = Guid.NewGuid().ToString("N");
    await db.Ado.ExecuteCommandAsync($$"""
        INSERT INTO task_switch_session
        (SwitchId,SourceTaskId,VehicleId,TargetStationId,TargetTaskId,Stage,ResumeStage,Message,BlockersJson,Revision,
         UpdatedAtUtc,MapId,MapVersionId,ParameterRevision,ReplacementRequestId,CancelRequestId,RequestJson,Operator,
         ReplacementCreateAttempted)
        VALUES ('{{id}}','LEGACY-SOURCE','CAR-X','TARGET',NULL,'{{TaskSwitchStages.Requested}}',NULL,'legacy','[]',1,
                '{{DateTime.UtcNow:O}}','MAP','V1','P1','{{Guid.NewGuid():N}}','{{Guid.NewGuid():N}}','{}','legacy',0)
        """);
    await new DatabaseInitializer(db).InitializeAsync(default);
    var migrated = await db.Queryable<TaskSwitchEntity>().SingleAsync(row => row.SwitchId == id);
    Require(migrated.ResolvedMode == TaskSwitchModes.CancelEmptyNavigation, "legacy request remains empty-navigation cancellation");
    try { File.Delete(path); } catch (IOException) { }
}

static Task HandoffContract()
{
    var site = new TaskHandoffSite("HANDOFF-01", "LM70", "HANDOFF-PLC", "HANDOFF-BAY-01",
        "handoff-v1", ["CAR-1"], true, ["CAR-2"],
        new("AtHandoff", "TransferReady", "BoxTaken", "TransferComplete", "ReceiverLoaded"),
        true, true, true, "COMM-HANDOFF-20261102");
    var options = new TaskHandoffOptions { Enabled = true, Sites = [site] };
    Require(options.Validate(site).Length == 0 && options.ReadySites().Single().HandoffId == "HANDOFF-01",
        "fully commissioned handoff contract passes");
    options.Enabled = false;
    Require(options.ReadySites().Length == 0, "disabled handoff configuration is not actionable");
    options.Enabled = true;
    var invalid = site with { ResourceId = "", RouteVerified = false, Signals = site.Signals with { BoxTaken = "" } };
    var errors = options.Validate(invalid);
    Require(errors.Any(error => error.Contains("资源")) && errors.Any(error => error.Contains("路线")) &&
        errors.Any(error => error.Contains("PLC")), "handoff configuration lists concrete blockers");
    options.Sites = [invalid];
    Require(options.ReadySites().Length == 0, "incomplete handoff never becomes ready");
    return Task.CompletedTask;
}

static TaskSwitchResumeRequest Review(TaskSwitchRecord current) => new(current.SwitchId, current.Revision, "模拟核对原车辆箱体PLC和路权责任已闭合", true);
static void Require(bool value, string message) { if (!value) throw new Exception("FAIL " + message); }

sealed class Fixture : IDisposable
{
    public ISqlSugarClient Db { get; }
    public SyntheticPort Port { get; }
    public TaskSwitchService Service { get; }
    public TaskSwitchGuard Guard { get; }
    public TrafficUserContext User { get; } = new("SIM\\operator", "isolated operator", TrafficPermission.View);
    private readonly string path;
    private Fixture(ISqlSugarClient db, string databasePath)
    { Db = db; path = databasePath; Port = new(db); Service = new(db, Port); Guard = new(db); }
    public static async Task<Fixture> Create()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "TaskSwitchSmoke-" + Guid.NewGuid().ToString("N") + ".db");
        var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = databasePath });
        await new DatabaseInitializer(db).InitializeAsync(default);
        var fixture = new Fixture(db, databasePath);
        await fixture.Service.InitializeAsync(default);
        return fixture;
    }
    public TaskSwitchRequest Request(string source = "SOURCE-1") => new(Guid.NewGuid().ToString("N"), source, DispatchTaskStage.GoingToPickup,
        "TARGET-CONFIGURED", 80, "隔离模拟空车停稳后核对原责任并切换新任务", true);
    public async Task<TaskSwitchRecord> Start()
    {
        var result = await Service.RequestAsync(User, Request(), default);
        if (!result.Accepted || result.Current is null) throw new Exception(result.Message);
        return result.Current;
    }
    public async Task<TaskSwitchRecord> Current(string id) => (await Service.GetAsync(id, default))!;
    public async Task PumpUntil(string id, string stage)
    {
        for (var i = 0; i < 12; i++)
        {
            if ((await Current(id)).Stage == stage) return;
            await Service.ProcessAsync(default);
        }
        throw new Exception("expected " + stage + ", actual " + (await Current(id)).Stage);
    }
    public void Dispose()
    {
        Db.Dispose();
        // Exact generated fixture files only. Some providers retain pooled handles until process exit.
        foreach (var file in new[] { path, path + "-shm", path + "-wal" })
            try { File.Delete(file); } catch (IOException) { }
    }
}

sealed class SyntheticPort(ISqlSugarClient database) : ITaskSwitchPort
{
    public string[] InspectionBlockers = [], ObservationBlockers = [];
    public string Version = "MAP-V1", VehicleId = "CAR-X";
    public string InspectionMode = TaskSwitchModes.CancelEmptyNavigation;
    public bool CancelConfirmed, CleanupConfirmed, OriginalClosed, CanClose, ThrowCancelOnce, ThrowCreateOnce, PersistCreate = true;
    public int CancelCalls, CloseCalls, CreateCalls;
    public List<string> CancelKeys = [], CreateKeys = [];
    public Task<TaskSwitchInspection> InspectSourceAsync(TaskSwitchRequest request, CancellationToken ct) =>
        Task.FromResult(new TaskSwitchInspection(VehicleId, "MAP-X", Version, "PARAM-1", InspectionBlockers)
            { ResolvedMode = InspectionMode });
    public Task RequestCancelAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        CancelCalls++; CancelKeys.Add(session.CancelRequestId);
        if (ThrowCancelOnce) { ThrowCancelOnce = false; throw new IOException("synthetic lost cancel response"); }
        return Task.CompletedTask;
    }
    public Task<TaskSwitchClosureObservation> ObserveCloseAsync(TaskSwitchRecord session, CancellationToken ct) =>
        Task.FromResult(new TaskSwitchClosureObservation(CancelConfirmed, OriginalClosed, "MAP-X", Version, "PARAM-1", ObservationBlockers)
        { CanCloseOriginal = CanClose, CleanupConfirmed = CleanupConfirmed });
    public Task CloseOriginalAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        if (!CancelConfirmed || !CanClose) throw new Exception("synthetic close requires original evidence");
        CloseCalls++; OriginalClosed = true;
        return Task.CompletedTask;
    }
    public async Task<string> CreateReplacementAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        CreateCalls++; CreateKeys.Add(session.ReplacementRequestId);
        var taskId = "NEW-" + session.ReplacementRequestId;
        if (PersistCreate)
        {
            await database.Insertable(new DispatchTaskEntity
            {
                TaskId = taskId, StationId = session.TargetStationId, RequestedVehicleId = session.VehicleId,
                CreateRequestId = session.ReplacementRequestId, Stage = (int)DispatchTaskStage.Created,
                CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
            }).ExecuteCommandAsync(ct);
            await database.Insertable(new DispatchTaskStepEntity
            {
                TaskId = taskId, StepId = "STEP-" + Guid.NewGuid().ToString("N"), Sequence = 1,
                StepType = (int)DispatchTaskStepType.GoToPickup, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
            }).ExecuteCommandAsync(ct);
        }
        if (ThrowCreateOnce) { ThrowCreateOnce = false; throw new IOException("synthetic lost create response"); }
        return PersistCreate ? taskId : throw new IOException("synthetic missing receipt");
    }
    public async Task<string?> FindReplacementAsync(TaskSwitchRecord session, CancellationToken ct) =>
        (await database.Queryable<DispatchTaskEntity>().FirstAsync(t => t.CreateRequestId == session.ReplacementRequestId, ct))?.TaskId;
}
