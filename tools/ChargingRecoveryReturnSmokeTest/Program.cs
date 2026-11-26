using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using SqlSugar;

// B07-FIX C6: restart recovery of a charging session that is leaving its bay (-EXIT) or returning (-RETURN).
var checks = 0;
void Check(bool condition, string name)
{ if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }

await using (var f = await Fixture.Create(ChargingPhase.Exiting, ("EXIT", ["BAY", "ROAD-OUT"])))
{
    await f.Boot(); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen, "crash during exit stays frozen until the physical stop is reviewed");
    await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(f.Barrier.IsOpen && f.Gateway.Sends == 0 && f.Gateway.Cancels == 0,
        "crash during exit: the -EXIT step with the bay is accepted and closed without any device command");
}
await using (var f = await Fixture.Create(ChargingPhase.Exiting, ("EXIT", ["ROAD-OUT"])))
{
    await f.Boot(); await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(f.Barrier.IsOpen, "crash after the bay was released at the exit: the -EXIT step without the bay is accepted");
}
await using (var f = await Fixture.Create(ChargingPhase.Returning, ("RETURN", ["ROAD-RET"])))
{
    await f.Boot(); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen, "crash during return stays frozen until the standby arrival is reviewed");
    await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(f.Barrier.IsOpen && f.Gateway.Sends == 0, "crash during return: the -RETURN lease is accepted and closed at the standby point");
}
await using (var f = await Fixture.Create(ChargingPhase.Returning, ("RETURN", ["ROAD-RET"])))
{
    await f.Boot(); await f.Complete(atStandby: false); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && (await f.Recovery.GetAsync(default)).Items.Single().Reason.Contains("返回待机点或退出点"),
        "a return session closed at the exit instead of the standby point is not verified");
}
await using (var f = await Fixture.Create(ChargingPhase.Charging, ("EXIT", ["ROAD-OUT"])))
{
    await f.Boot(); await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && (await f.Recovery.GetAsync(default)).Items.Single().Reason.Contains("原充电租约"),
        "a session that has not left its bay must still hold the bay lease");
}
await using (var f = await Fixture.Create(ChargingPhase.Returning, ("RETURN", ["FOREIGN"])))
{
    await f.Boot(); await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && (await f.Recovery.GetAsync(default)).Items.Single().Reason.Contains("原充电租约"),
        "a lease on a resource outside the planned route is rejected");
}
await using (var f = await Fixture.Create(ChargingPhase.Returning, ("OTHER", ["ROAD-RET"])))
{
    await f.Boot(); await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && (await f.Recovery.GetAsync(default)).Items.Single().Reason.Contains("原充电租约"),
        "a lease from an unplanned step is rejected");
}
await using (var f = await Fixture.Create(ChargingPhase.Returning, ("RETURN", ["ROAD-RET"])))
{
    var lease = f.Db.Queryable<TrafficResourceLeaseEntity>().Single();
    lease.VehicleId = "OTHER";
    await f.Db.Updateable(lease).ExecuteCommandAsync();
    await f.Boot(); await f.Complete(); await f.Recovery.InspectAsync(default);
    Check(!f.Barrier.IsOpen && (await f.Recovery.GetAsync(default)).Items.Single().Reason.Contains("原充电租约"),
        "a return lease owned by another vehicle is rejected");
}
await using (var f = await Fixture.CreateWaiting())
{
    await f.Boot();
    Check(!f.Barrier.IsOpen, "a waiting-site session alone requires a fresh physical-exit review after restart");
    await f.CompleteWaiting();
    await f.Recovery.InspectAsync(default);
    var waitStatus = await f.Recovery.GetAsync(default);
    Check(f.Barrier.IsOpen && f.Gateway.Sends == 0 && f.Gateway.Cancels == 0,
        "a verified waiting-site exit releases parking and opens recovery without resending navigation: " +
        string.Join(" | ", waitStatus.Items.Select(item => item.Reason)));
}
Console.WriteLine($"Charging return recovery smoke passed: {checks} checks; isolated SQLite and durable JSON only.");

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
    public static async Task<Fixture> CreateWaiting()
    {
        var f = await Create(ChargingPhase.GoingToWaitingSite, ("PARK", ["ROAD-RET", "BAY"]));
        f.Session = f.Session with
        {
            Site = null, Route = null,
            WaitingRoute = new("MAP", "V1", ["HOME", "SAFE"], "BAY", ["ROAD-RET", "BAY"], 2),
            CommandId = f.Session.SessionId + "-WAIT", VendorTaskId = f.Session.SessionId + "-WAIT"
        };
        f.Store.Save([f.Session]);
        return f;
    }
    public async Task CompleteWaiting()
    {
        await Traffic.ReleaseTaskAsync(Session.SessionId, default);
        var now = DateTimeOffset.Now;
        Session = Session with { Phase = ChargingPhase.Completed, UpdatedAt = now, Operator = "fixture\engineer",
            OperatorEvidence = "本启动后现场确认离开等桩路线与等待位", Battery = new("AGV-01", 24, false, 0, 30, false, false, now) };
        Store.Save([Session]); Place("OUTSIDE", 4);
    }
    public static async Task<Fixture> Create(ChargingPhase phase, params (string Step, string[] Resources)[] leases)
    {
        var path = Path.Combine(Path.GetTempPath(), "agv-charge-return-" + Guid.NewGuid().ToString("N"));
        var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(path, "test.db") });
        var catalog = new TrafficResourceCatalog([new("BAY", TrafficResourceType.ChargingBay, 1),
            new("ROAD-IN", TrafficResourceType.SingleLane), new("ROAD-OUT", TrafficResourceType.SingleLane),
            new("ROAD-RET", TrafficResourceType.SingleLane), new("FOREIGN", TrafficResourceType.SingleLane)]);
        await new DatabaseInitializer(db, catalog).InitializeAsync(default);
        var store = new ChargingSessionStore(Path.Combine(path, "charging.json"));
        var session = new ChargingSession("CHARGE-" + Guid.NewGuid().ToString("N"), "AGV-01", phase,
            DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, "synthetic charging")
        {
            Site = new("BAY", "CHARGER", "EXIT", "BAY") { Commissioned = true, AllowedVehicleIds = ["AGV-01"] },
            Route = new("MAP", "V1", ["HOME", "CHARGER"], ["CHARGER", "EXIT"], ["BAY", "ROAD-IN", "ROAD-OUT", "ROAD-RET"], 2)
            { ReturnSiteId = "STANDBY", ReturnPath = ["EXIT", "STANDBY"] },
            StartAttempted = true
        };
        store.Save([session]);
        var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromMinutes(-1)));
        foreach (var (step, resources) in leases)
        {
            var stepId = session.SessionId + "-" + step;
            await traffic.RecordRouteAsync(new(stepId, session.SessionId, "AGV-01", "HOME", "EXIT", [], resources, false, "synthetic charging")
                { MapId = "MAP", MapVersionId = "V1" }, default);
            if (!(await traffic.TryAcquireAsync(stepId, session.SessionId, "AGV-01", resources, default)).Acquired)
                throw new Exception("fixture acquire failed: " + step);
        }
        var f = new Fixture { Db = db, Traffic = traffic, Store = store, Session = session, Recovery = null! };
        var gate = new MapExecutionGate(f.Barrier); gate.SetMode(MapExecutionMode.Running);
        var map = new ActiveMapRuntime();
        map.Replace(new("MAP", "V1", new("synthetic", "fixture", "world", "m",
            [new("HOME", 0, 0, MapNodeType.WaitingPoint, true, [], "HOME") { AllowTrafficStop = true },
             new("CHARGER", 1, 0, MapNodeType.ChargingPoint, true, ["BAY"], "CHARGER"),
             new("EXIT", 2, 0, MapNodeType.WaitingPoint, true, [], "EXIT"),
             new("STANDBY", 3, 0, MapNodeType.WaitingPoint, true, [], "STANDBY"),
             new("OUTSIDE", 4, 0, MapNodeType.WaitingPoint, true, [], "OUTSIDE") { AllowTrafficStop = true }],
            [new("IN", "HOME", "CHARGER", 1, 0.5, false, true, ["ROAD-IN"]),
             new("OUT", "CHARGER", "EXIT", 1, 0.5, false, true, ["ROAD-OUT"]),
             new("RET", "EXIT", "STANDBY", 1, 0.5, false, true, ["ROAD-RET"]),
             new("OUTSIDE-LINK", "STANDBY", "OUTSIDE", 1, 0.5, false, true, ["ROAD-RET"])],
            [new("BAY", TrafficResourceType.ChargingBay, 1), new("ROAD-IN", TrafficResourceType.SingleLane, 1),
             new("ROAD-OUT", TrafficResourceType.SingleLane, 1), new("ROAD-RET", TrafficResourceType.SingleLane, 1)]), DateTimeOffset.Now));
        f.Recovery = new(db, f.Barrier, gate, f.Gateway, f.Vehicles, new SwitchSignalCache(), new(), map, chargingRecovery: store);
        return f;
    }
    public async Task Boot()
    { await Recovery.InitializeAsync(default); Place("EXIT", 2); }
    private void Place(string station, double x)
    {
        Vehicles.Items.Clear();
        Vehicles.Items.Add(SwitchVehicleCache.Ready() with { CurrentStation = station, X = x, Y = 0, Charging = false, BatteryCurrent = 0 });
    }
    public async Task Complete(bool atStandby = true)
    {
        await Traffic.ReleaseTaskAsync(Session.SessionId, default);
        var now = DateTimeOffset.Now;
        Session = Session with { Phase = ChargingPhase.Completed, UpdatedAt = now, Operator = "fixture\\engineer", OperatorEvidence = "本启动后现场确认停止充电、空载及实际离位", Battery = new("AGV-01", 80, false, 0, 30, false, false, now) };
        Store.Save([Session]);
        if (atStandby) Place("STANDBY", 3); else Place("EXIT", 2);
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
