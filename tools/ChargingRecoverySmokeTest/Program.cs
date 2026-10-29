using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using SqlSugar;

var checks = 0;
void Check(bool condition, string name)
{ if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }

await using (var f = await Fixture.Create())
{
    Check(f.Db.Queryable<DispatchTaskEntity>().Count() == 0 && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 1,
        "独立充电会话不伪造业务任务，真实交通管理器仍持久占用");
    await f.Boot();
    await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 1, "充电重启先冻结，过期原租约不自动释放");
    Check((await f.Traffic.GetUnavailableResourceIdsAsync("OTHER", default)).Contains("BAY"), "重启后其他任务仍被原充电位互斥阻挡");
    Check((await f.Recovery.GetAsync(default)).Items.Single().VehicleId == "AGV-01", "恢复页面保留独立充电会话车辆归属");
    await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(f.Barrier.IsOpen && f.Gateway.Sends == 0 && f.Gateway.Cancels == 0, "本启动现场关闭且实际退出后核定原票据，恢复不发设备动作：" + (await f.Recovery.GetAsync(default)).Items.Single().Reason);
    Check((await f.Recovery.GetAsync(default)).Audits.Any(a => a.Decision == "VerifiedChargingClosed"), "专项关闭核定审计持久化");
}

await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete(release: false); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 1, "Completed但原租约未释放仍阻断");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Traffic.ReleaseTaskAsync(f.Session.SessionId, default); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen, "原资源消失但会话未完成核对仍阻断");
}
await using (var f = await Fixture.Create())
{
    f.Store.Save([]); await f.Boot(); await f.Traffic.ReleaseTaskAsync(f.Session.SessionId, default); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen, "仅CHARGE前缀不能绕过孤立占用恢复核对");
}
foreach (var fault in new[] { "vehicle", "step", "map", "resource" })
    await using (var f = await Fixture.Create())
    {
        var lease = f.Db.Queryable<TrafficResourceLeaseEntity>().Single();
        if (fault == "vehicle") lease.VehicleId = "OTHER";
        if (fault == "step") lease.OwnerStepId = "OTHER";
        if (fault == "map") lease.MapVersionId = "OTHER";
        if (fault == "resource") lease.ResourceId = "OTHER";
        await f.Db.Updateable(lease).ExecuteCommandAsync();
        await f.Boot(); await f.Complete(); await f.Recovery.InspectAsync(default);
        Check(!f.Barrier.IsOpen, "Boot原租约归属不符不能凭会话Completed放行：" + fault);
    }
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete();
    f.Store.Save([f.Session with { VehicleId = "OTHER" }]);
    await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen, "关闭记录车辆变化不能替代原车辆");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete();
    await f.Db.Deleteable<ExecutionEvidenceEntity>().Where(e => e.Kind == "ChargingBootOwnership").ExecuteCommandAsync();
    await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen, "启动归属快照缺失阻断自动核定");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete(); f.Store.Save([f.Session with { OperatorEvidence = null }]);
    await f.Recovery.InspectAsync(default); Check(!f.Barrier.IsOpen, "缺少本次现场核对依据不能放行");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete(); f.Store.Save([f.Session with { UpdatedAt = DateTimeOffset.Now.AddDays(-1) }]);
    await f.Recovery.InspectAsync(default); Check(!f.Barrier.IsOpen, "不能沿用启动前的充电关闭证据");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete();
    await f.Db.Insertable(new AgvCommandOutboxEntity { CommandId = "unexpected", TaskId = f.Session.SessionId, StepId = "unexpected", VehicleId = "AGV-01", PayloadJson = "{}" }).ExecuteCommandAsync();
    await f.Recovery.InspectAsync(default); Check(!f.Barrier.IsOpen, "原会话出现业务Outbox命令仍阻断");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete(); f.Vehicles.Items[0] = f.Vehicles.Items[0] with { X = 2, CurrentStation = "CHARGER" };
    await f.Recovery.InspectAsync(default); Check(!f.Barrier.IsOpen, "Completed记录不能替代实际退出位置");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); await f.Complete(); f.Vehicles.Items[0] = f.Vehicles.Items[0] with { Charging = true };
    await f.Recovery.InspectAsync(default); Check(!f.Barrier.IsOpen, "实际仍在充电不能核定专项关闭");
}
Console.WriteLine($"Charging recovery smoke passed: {checks} checks; isolated SQLite and durable JSON only.");

sealed class Fixture : IAsyncDisposable
{
    public required ISqlSugarClient Db;
    public required TrafficResourceManager Traffic;
    public required ChargingSessionStore Store;
    public required ChargingSession Session;
    public required ExecutionRecoveryService Recovery;
    public RecoveryBarrier Barrier = new();
    public SwitchVehicleCache Vehicles = new();
    public Gateway Gateway = new();
    public static async Task<Fixture> Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "agv-charge-recovery-" + Guid.NewGuid().ToString("N"));
        var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(path, "test.db") });
        var catalog = new TrafficResourceCatalog([new("BAY", TrafficResourceType.ChargingBay, 1)]);
        await new DatabaseInitializer(db, catalog).InitializeAsync(default);
        var store = new ChargingSessionStore(Path.Combine(path, "charging.json"));
        var session = new ChargingSession("CHARGE-" + Guid.NewGuid().ToString("N"), "AGV-01", ChargingPhase.Charging,
            DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, "synthetic charging")
        {
            Site = new("BAY", "CHARGER", "EXIT", "BAY") { Commissioned = true, AllowedVehicleIds = ["AGV-01"] },
            Route = new("MAP", "V1", ["HOME", "CHARGER"], ["CHARGER", "EXIT"], ["BAY"], 2), StartAttempted = true
        };
        store.Save([session]);
        var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromMinutes(-1)));
        await traffic.RecordRouteAsync(new(session.SessionId + "-LOCK", session.SessionId, "AGV-01", "HOME", "EXIT", [], ["BAY"], false, "synthetic charging")
            { MapId = "MAP", MapVersionId = "V1" }, default);
        if (!(await traffic.TryAcquireAsync(session.SessionId + "-LOCK", session.SessionId, "AGV-01", ["BAY"], default)).Acquired) throw new Exception("fixture acquire failed");
        var f = new Fixture { Db = db, Traffic = traffic, Store = store, Session = session, Recovery = null! };
        var gate = new MapExecutionGate(f.Barrier); gate.SetMode(MapExecutionMode.Running);
        var map = new ActiveMapRuntime();
        map.Replace(new("MAP", "V1", new("synthetic", "fixture", "world", "m",
            [new("HOME", 0, 0, MapNodeType.WaitingPoint, true, [], "HOME"), new("CHARGER", 1, 0, MapNodeType.ChargingPoint, true, ["BAY"], "CHARGER"), new("EXIT", 2, 0, MapNodeType.WaitingPoint, true, [], "EXIT")],
            [new("IN", "HOME", "CHARGER", 1, 0.5, false, true, ["BAY"]), new("OUT", "CHARGER", "EXIT", 1, 0.5, false, true, ["BAY"])],
            [new("BAY", TrafficResourceType.ChargingBay, 1)]), DateTimeOffset.Now));
        f.Recovery = new(db, f.Barrier, gate, f.Gateway, f.Vehicles, new SwitchSignalCache(), new(), map, chargingRecovery: store);
        return f;
    }
    public async Task Boot()
    { await Recovery.InitializeAsync(default); Fresh(); }
    private void Fresh()
    { Vehicles.Items.Clear(); Vehicles.Items.Add(SwitchVehicleCache.Ready() with { CurrentStation = "EXIT", X = 2, Y = 0, Charging = false, BatteryCurrent = 0 }); }
    public async Task Complete(bool release = true)
    {
        if (release) await Traffic.ReleaseTaskAsync(Session.SessionId, default);
        var now = DateTimeOffset.Now;
        Session = Session with { Phase = ChargingPhase.Completed, UpdatedAt = now, Operator = "fixture\\engineer", OperatorEvidence = "本启动后现场确认停止充电、空载及实际离位", Battery = new("AGV-01", 80, false, 0, 30, false, false, now) };
        Store.Save([Session]); Fresh();
    }
    public ValueTask DisposeAsync() { Db.Dispose(); return ValueTask.CompletedTask; }
}

sealed class Gateway : IAgvGateway
{
    public int Sends, Cancels;
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct) { Sends++; throw new NotSupportedException(); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task CancelTaskAsync(string id, CancellationToken ct) { Cancels++; throw new NotSupportedException(); }
}
