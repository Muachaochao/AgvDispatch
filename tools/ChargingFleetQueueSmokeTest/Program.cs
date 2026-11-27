using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

// Charging queue priority (C5), fleet policy (C3) and preemption (C4). Synthetic ports and SQLite only.
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
var evidence = Path.Combine(Path.GetTempPath(), "agv-charging-fleet-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);

// C5: a critical vehicle keeps its place at the head of the bay queue; a higher-battery request cannot take the bay first.
using (var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(evidence, "traffic.db") }))
{
    var catalog = new TrafficResourceCatalog([new("BAY", TrafficResourceType.ChargingBay, 1)]);
    await new DatabaseInitializer(database, catalog).InitializeAsync(default);
    var traffic = new TrafficResourceManager(database, catalog, new(TimeSpan.FromSeconds(1)));
    async Task<bool> Reserve(string session, string vehicle, double priority)
    {
        await traffic.RecordRouteAsync(new(session + "-LOCK", session, vehicle, "STATION", "CHARGER", [], ["BAY"], false, "synthetic charging")
        { MapId = "MAP", MapVersionId = "V1", QueuePriority = priority }, default);
        return (await traffic.TryAcquireAsync(session + "-LOCK", session, vehicle, ["BAY"], default)).Acquired;
    }
    const double critical = ChargingRuntimePort.CriticalChargingQueueTier;
    Check(await Reserve("CHARGE-HOLDER", "H", 0), "the bay is granted to the first charging request");
    Check(!await Reserve("CHARGE-HIGHER", "B", 0), "an ordinary charging request waits behind the holder");
    Check(!await Reserve("CHARGE-CRITICAL", "C", critical + 90), "a critical charging request queues behind the holder");
    await traffic.ReleaseTaskAsync("CHARGE-HOLDER", default);
    Check(!await Reserve("CHARGE-HIGHER", "B", 0), "after release the higher-battery request cannot take the bay ahead of the critical vehicle");
    Check(await Reserve("CHARGE-CRITICAL", "C", critical + 90), "the critical vehicle takes the released bay");
}

// C3: five vehicles at the start threshold and one bay. Only one vehicle is withdrawn; the others keep taking business.
var five = new Fleet(evidence, "five", ["A", "B", "C", "D", "E"], bays: 1);
foreach (var id in five.Vehicles) five.Put(id, 24);
await five.CyclesAsync(4);
var fleetSessions = five.Service.ReadAsync(default).GetAwaiter().GetResult().Sessions
    .Where(s => s.Phase != ChargingPhase.Completed).ToArray();
Check(fleetSessions.Length == five.Vehicles.Length && fleetSessions.Count(s => s.HasReservation) == 1,
    "five vehicles above critical with one bay: all remain visible, but only one reserves the bay and leaves business");
Check(five.Vehicles.All(v => !five.Service.AllowsNewBusiness(v)),
    "vehicles at or below the charging threshold remain protected from new business while they queue");
five.Put("E", 14);
await five.CyclesAsync(2);
Check(five.Service.ReadAsync(default).GetAwaiter().GetResult().Sessions.Any(s => s.VehicleId == "E" && s.Phase != ChargingPhase.Completed) &&
    !five.Service.AllowsNewBusiness("E"), "a vehicle below the critical threshold is always withdrawn, even when no bay is free");

// C3: a fleet minimum stops a second withdrawal even when a second bay is free.
var minimum = new Fleet(evidence, "minimum", ["A", "B", "C", "D", "E"], bays: 2, minimumAvailable: 4);
foreach (var id in minimum.Vehicles) minimum.Put(id, 24);
await minimum.CyclesAsync(4);
Check(minimum.Sessions().Count(s => s.HasReservation) == 1,
    "the minimum available fleet blocks a second bay reservation that would breach the floor");

// C3: the out-of-service cap limits simultaneous withdrawals.
var cap = new Fleet(evidence, "cap", ["A", "B", "C", "D", "E"], bays: 2, maximumOutOfService: 1);
foreach (var id in cap.Vehicles) cap.Put(id, 24);
await cap.CyclesAsync(4);
Check(cap.Sessions().Count(s => s.HasReservation) == 1,
    "the out-of-service cap allows no more than one reservation");

// C4: a critical vehicle waiting for the only bay asks a charging vehicle above the release level to stop.
Check(new ChargingOptions().PreemptionReleaseBatteryPercent == 50, "the default preemption release level is 50 percent");
var preempt = new Fleet(evidence, "preempt", ["A", "B"], bays: 1);
preempt.Put("A", 24);
await preempt.ToChargingAsync("A");
preempt.Put("A", 79, true, 4);
preempt.Put("B", 10);
await preempt.CyclesAsync(3);
Check(preempt.Session("A")!.StopRequested && preempt.Session("A")!.Phase is not ChargingPhase.Completed,
    "a charging vehicle at 79 percent is asked to stop when a critical vehicle waits for the bay");
Check(!preempt.Session("B")!.HasReservation,
    "the critical vehicle does not take the bay until the stop and exit flow completes");
preempt.Put("A", 79, false, 0);
await preempt.CyclesAsync(2);
preempt.Port.Arrive("A", "EXIT-1");
await preempt.CyclesAsync(2);
preempt.Port.Arrive("A", "STANDBY");
await preempt.CyclesAsync(3);
Check(preempt.Session("A") is null || preempt.Session("A")!.Phase == ChargingPhase.Completed, "the preempted vehicle completes its normal exit and return");
Check(preempt.Session("B")!.HasReservation, "after the preempted vehicle releases the bay, the critical vehicle reserves it");

var noPreempt = new Fleet(evidence, "no-preempt", ["A", "B"], bays: 1);
noPreempt.Put("A", 24);
await noPreempt.ToChargingAsync("A");
noPreempt.Put("A", 60, true, 4);
noPreempt.Put("B", 10);
await noPreempt.CyclesAsync(3);
Check(noPreempt.Session("A")!.StopRequested == false && noPreempt.Session("A")!.Phase == ChargingPhase.Charging,
    "a charging vehicle below the release level is never preempted");

// C12: a vehicle whose battery is unknown is not usable for new business and is not queued for charging.
var unknown = new Fleet(evidence, "unknown", ["U", "A"], bays: 1);
unknown.Put("U", 24);
unknown.Port.ForgetBattery("U");
unknown.Put("A", 10);
await unknown.CyclesAsync(2);
Check(unknown.Session("U") is null && !unknown.Service.AllowsNewBusiness("U"),
    "a vehicle with an unknown battery is neither queued for charging nor allowed new business");
unknown.Put("U", 24);
await unknown.CyclesAsync(1);
Check(unknown.Session("U") is { HasReservation: false } && !unknown.Service.AllowsNewBusiness("U"),
    "a vehicle whose battery reads again is queued for charging and remains protected from new business");

// C13: a manual request with a different letter case is stored under the vehicle id of the state cache.
var casing = new Fleet(evidence, "casing", ["A", "B"], bays: 2);
casing.Put("A", 60);
await casing.CyclesAsync(1);
var admin = AgvDispatch.Application.Abstractions.TrafficUserContext.LocalOperation("charging-fleet-test");
var request = await casing.Service.RequestAsync(admin, new(Guid.NewGuid().ToString("N"), "a"), default);
Check(request.Accepted && casing.Sessions().Count(s => s.VehicleId == "A") == 1 && casing.Sessions().All(s => s.VehicleId == "A"),
    "a request with a lower-case vehicle id is stored under the canonical vehicle id");

// B07-R8 business-first release: orders waiting with no free vehicle stop a vehicle that has reached the
// release level, through the normal stop and exit flow; no demand or a lower battery keeps it charging.
Check(new ChargingOptions().BusinessDemandReleaseBatteryPercent == 0, "business-first release is off by default");
Check(new ChargingOptions { Sites = [], BusinessDemandReleaseBatteryPercent = 20 }.ConfigurationError() is not null &&
      new ChargingOptions { Sites = [], BusinessDemandReleaseBatteryPercent = 80 }.ConfigurationError() is not null &&
      new ChargingOptions { Sites = [], BusinessDemandReleaseBatteryPercent = 40 }.ConfigurationError() is null,
    "the business-first release level must lie between the start and target levels");
var business = new Fleet(evidence, "business", ["A", "B"], bays: 1, businessRelease: 40);
business.Put("A", 24);
await business.ToChargingAsync("A");
business.Put("A", 45, true, 4);
business.Put("B", 60);
business.Port.Demand = 0;
await business.CyclesAsync(3);
Check(business.Session("A")!.StopRequested == false && business.Session("A")!.Phase == ChargingPhase.Charging,
    "without waiting orders a vehicle above the release level keeps charging to the target");
business.Port.Demand = 2;
business.Put("A", 35, true, 4);
await business.CyclesAsync(3);
Check(business.Session("A")!.StopRequested == false, "below the release level a vehicle keeps charging even when orders wait");
business.Put("A", 45, true, 4);
await business.CyclesAsync(3);
Check(business.Session("A")!.StopRequested && business.Session("A")!.Phase is not ChargingPhase.Completed,
    "orders waiting with no free vehicle: the vehicle at 45 percent is asked to stop through the normal flow");
business.Put("A", 45, false, 0);
await business.CyclesAsync(2);
business.Port.Arrive("A", "EXIT-1");
await business.CyclesAsync(2);
business.Port.Arrive("A", "STANDBY");
await business.CyclesAsync(3);
Check(business.Session("A") is null || business.Session("A")!.Phase == ChargingPhase.Completed,
    "the released vehicle completes the normal exit and becomes available for business");
var businessOff = new Fleet(evidence, "business-off", ["A", "B"], bays: 1);
businessOff.Put("A", 24);
await businessOff.ToChargingAsync("A");
businessOff.Put("A", 45, true, 4);
businessOff.Port.Demand = 3;
await businessOff.CyclesAsync(3);
Check(businessOff.Session("A")!.StopRequested == false, "with the option off, waiting orders never stop charging early");

Console.WriteLine($"PASS: {checks} charging queue and fleet checks.");

sealed class Fleet
{
    public FakeClock Clock { get; } = new();
    public FleetPort Port { get; }
    public FleetRobot Robot { get; }
    public ChargingService Service { get; }
    public string[] Vehicles { get; }
    public Fleet(string root, string name, string[] vehicles, int bays, int minimumAvailable = 0, int maximumOutOfService = 0, decimal businessRelease = 0)
    {
        Vehicles = vehicles;
        Port = new(Clock); Robot = new(Clock);
        var sites = Enumerable.Range(1, bays).Select(i => new ChargingSite("BAY-" + i, "CHARGER-" + i, "EXIT-" + i, "BAY-" + i)
            { Commissioned = true, AllowedVehicleIds = vehicles }).ToArray();
        var options = new ChargingOptions { Enabled = true, Sites = sites, DefaultStandbySiteId = "STANDBY",
            MinimumAvailableVehicles = minimumAvailable, MaximumVehiclesOutOfService = maximumOutOfService,
            BusinessDemandReleaseBatteryPercent = businessRelease };
        Service = new(options, Port, Robot, Path.Combine(root, name + ".json"), NullLogger<ChargingService>.Instance, Clock);
    }
    public void Put(string vehicle, decimal percent, bool charging = false, double current = 0)
    {
        Port.Put(vehicle, percent, charging, current);
        Robot.Replace(vehicle, new(vehicle, percent, charging, current, 25, false, false, Clock.GetUtcNow()));
    }
    public ChargingSession? Session(string vehicle) => Service.ReadAsync(default).GetAwaiter().GetResult().Sessions
        .LastOrDefault(s => s.VehicleId == vehicle);
    public string? Reserved() => Vehicles.FirstOrDefault(v => Session(v) is { HasReservation: true });
    public IReadOnlyList<ChargingSession> Sessions() => Service.ReadAsync(default).GetAwaiter().GetResult().Sessions
        .Where(s => s.Phase != ChargingPhase.Completed).ToArray();
    public async Task CyclesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Port.Refresh();
            foreach (var id in Vehicles) Robot.Set(id, Clock.GetUtcNow());
            await Service.RunCycleAsync(default);
        }
    }
    public async Task ToChargingAsync(string vehicle)
    {
        for (var i = 0; i < 20 && Session(vehicle)?.Phase != ChargingPhase.GoingToCharger; i++) await CyclesAsync(1);
        Port.Arrive(vehicle, "CHARGER-1");
        for (var i = 0; i < 20 && Session(vehicle)?.Phase != ChargingPhase.AwaitingCurrent; i++) await CyclesAsync(1);
        Put(vehicle, 20, true, 4);
        for (var i = 0; i < 20 && Session(vehicle)?.Phase != ChargingPhase.Charging; i++) await CyclesAsync(1);
    }
}

sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class FleetRobot(FakeClock clock) : IChargingRobotControl
{
    private readonly Dictionary<string, ChargingBatteryReading> readings = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Vehicle, bool On)> RelayWrites { get; } = [];
    public void Set(string vehicle, DateTimeOffset observed) => readings[vehicle] = Current(vehicle) with { ObservedAt = observed };
    public ChargingBatteryReading Current(string vehicle) => readings.TryGetValue(vehicle, out var reading) ? reading : new(vehicle, 24, false, 0, 25, false, false, clock.GetUtcNow());
    public void Replace(string vehicle, ChargingBatteryReading reading) => readings[vehicle] = reading;
    public Task SetChargingRelayAsync(string vehicleId, bool enabled, CancellationToken ct)
    {
        RelayWrites.Add((vehicleId, enabled));
        return Task.CompletedTask;
    }
    public Task<ChargingBatteryReading> ReadBatteryAsync(string vehicleId, CancellationToken ct) => Task.FromResult(Current(vehicleId) with { VehicleId = vehicleId });
}

// Each vehicle has its own station, and each bay has one charging exit. Leases are held per session and step.
sealed class FleetPort(FakeClock clock) : IChargingRuntimePort
{
    private readonly Dictionary<string, AgvRobotSnapshot> vehicles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Step, string Session, string Resource)> leases = [];
    public List<(string Command, string Vehicle)> Navigations { get; } = [];
    public HashSet<string> Completed { get; } = [];
    public IReadOnlyList<AgvRobotSnapshot> Vehicles => vehicles.Values.ToArray();
    public string? NewWorkBlockReason => null;
    public int Demand { get; set; }
    public Task<int> UnservedBusinessTasksAsync(IReadOnlySet<string> outOfService, CancellationToken ct) => Task.FromResult(Demand);
    public void Put(string id, decimal percent, bool charging = false, double current = 0)
    {
        stations.TryAdd(id, "HOME-" + id);
        vehicles[id] = new(id, id, "127.0.0.1", 19204, ConnectionState.Online, SignalQuality.Good,
            0, 0, 0, 1, stations[id], null, percent, false, false, "Idle", null, null, null, [], [],
            clock.GetUtcNow(), clock.GetUtcNow(), 0, 1, null, 1)
        { VelocityX = 0, VelocityY = 0, AngularVelocity = 0, Charging = charging, BatteryCurrent = current, BatteryTemperature = 25 };
    }
    public void ForgetBattery(string id) => vehicles[id] = vehicles[id] with { BatteryPercent = null };
    public void Arrive(string vehicle, string station)
    {
        stations[vehicle] = station;
        Completed.UnionWith(Navigations.Where(n => n.Vehicle == vehicle).Select(n => n.Command));
    }
    public void Refresh()
    {
        foreach (var id in vehicles.Keys.ToArray())
            vehicles[id] = vehicles[id] with { CurrentStation = stations[id], LastSuccessfulPollAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
    }
    private bool Take(string step, string session, string[] resources)
    {
        if (resources.Any(r => leases.Any(l => l.Resource == r && l.Session != session))) return false;
        foreach (var resource in resources)
            if (!leases.Any(l => l.Step == step && l.Resource == resource)) leases.Add((step, session, resource));
        return true;
    }
    public ValueTask<IDisposable?> EnterExecutionAsync(bool newWork, CancellationToken ct) => ValueTask.FromResult<IDisposable?>(new Lease());
    public ValueTask<IDisposable?> EnterRecoveryControlAsync(CancellationToken ct) => ValueTask.FromResult<IDisposable?>(new Lease());
    public Task<string?> BusyReasonAsync(string vehicleId, string? ownSessionId, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<ChargingRoute?> PlanAsync(string vehicleId, ChargingSite site, CancellationToken ct) => PlanWithReturnAsync(vehicleId, site, null, ct);
    public Task<ChargingRoute?> PlanWithReturnAsync(string vehicleId, ChargingSite site, string? returnSiteId, CancellationToken ct)
    {
        var outResource = "OUT-" + site.Id;
        return Task.FromResult<ChargingRoute?>(new("SYNTHETIC-MAP", "V1", [stations[vehicleId], site.ChargingSiteId],
            [site.ChargingSiteId, site.ExitSiteId], [site.ResourceId, outResource, "RET"], 5)
        { OriginSiteId = stations[vehicleId], ReturnSiteId = "STANDBY", ReturnPath = [site.ExitSiteId, "STANDBY"], InboundResourceIds = [site.ResourceId] });
    }
    public Task<string?> ValidateAsync(ChargingSession session, bool requireStopped, CancellationToken ct) => Task.FromResult<string?>(null);
    public bool IsAt(string vehicleId, string siteId) => stations.GetValueOrDefault(vehicleId) == siteId;
    public Task<bool> ReserveAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(Take(session.SessionId + "-LOCK", session.SessionId, [session.Site!.ResourceId]));
    public Task<int> ShrinkToBayAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(0);
    public Task<bool> AcquireExitAsync(ChargingSession session, CancellationToken ct) =>
        Task.FromResult(Take(session.SessionId + "-EXIT", session.SessionId, [session.Site!.ResourceId, "OUT-" + session.Site.Id]));
    public Task<bool> AcquireReturnAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(Take(session.SessionId + "-RETURN", session.SessionId, ["RET"]));
    public Task<int> ReleaseBayAtExitAsync(ChargingSession session, CancellationToken ct)
    {
        var removed = leases.RemoveAll(l => l.Session == session.SessionId && l.Step == session.SessionId + "-LOCK");
        removed += leases.RemoveAll(l => l.Session == session.SessionId && l.Step == session.SessionId + "-EXIT" && l.Resource == session.Site!.ResourceId);
        return Task.FromResult(removed);
    }
    public Task RenewAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseAsync(string sessionId, CancellationToken ct)
    {
        leases.RemoveAll(l => l.Session == sessionId);
        return Task.CompletedTask;
    }
    public Task<string> NavigateAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct)
    {
        Navigations.Add((commandId, vehicleId));
        return Task.FromResult(commandId);
    }
    public Task<AgvTransportTaskStatus?> ObserveNavigationAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct) =>
        Task.FromResult<AgvTransportTaskStatus?>(new(commandId, commandId,
            Completed.Contains(commandId) ? AgvTransportTaskState.Completed : AgvTransportTaskState.GoingToDestination, vehicleId, clock.GetUtcNow()));
    private sealed class Lease : IDisposable { public void Dispose() { } }
}
