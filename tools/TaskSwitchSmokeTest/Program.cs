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
    ("stale-resume-revision-rejected", StaleResume)
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
    public string Version = "MAP-V1";
    public bool CancelConfirmed, OriginalClosed, CanClose, ThrowCancelOnce, ThrowCreateOnce, PersistCreate = true;
    public int CancelCalls, CloseCalls, CreateCalls;
    public List<string> CancelKeys = [], CreateKeys = [];
    public Task<TaskSwitchInspection> InspectSourceAsync(TaskSwitchRequest request, CancellationToken ct) =>
        Task.FromResult(new TaskSwitchInspection("CAR-X", "MAP-X", Version, "PARAM-1", InspectionBlockers));
    public Task RequestCancelAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        CancelCalls++; CancelKeys.Add(session.CancelRequestId);
        if (ThrowCancelOnce) { ThrowCancelOnce = false; throw new IOException("synthetic lost cancel response"); }
        return Task.CompletedTask;
    }
    public Task<TaskSwitchClosureObservation> ObserveCloseAsync(TaskSwitchRecord session, CancellationToken ct) =>
        Task.FromResult(new TaskSwitchClosureObservation(CancelConfirmed, OriginalClosed, "MAP-X", Version, "PARAM-1", ObservationBlockers)
        { CanCloseOriginal = CanClose });
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
