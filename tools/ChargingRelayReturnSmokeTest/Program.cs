using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

// Relay-mode state machine tests with synthetic ports. No robot, PLC, database or service is contacted.
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS " + description);
}

Check(new ChargingOptions().ConfigurationError() is null, "default charging options remain valid without a standby point");
Check(!new ChargingOptions().ReturnToStandbyAfterCharging,
    "new charging sessions are dispatchable at the safe exit by default");
Check(new ChargingOptions { Sites = [new("BAY-1", "CHARGER", "EXIT", "BAY") { Commissioned = true, AllowedVehicleIds = ["A"] }], DefaultStandbySiteId = "EXIT" }
    .ConfigurationError() is not null, "a standby point equal to a charging exit is rejected");
Check(new ChargingOptions { Sites = [new("BAY-1", "CHARGER", "EXIT", "BAY") { Commissioned = true, AllowedVehicleIds = ["A"] }], DefaultStandbySiteId = "STANDBY" }
    .ConfigurationError() is null, "a dedicated standby point is accepted");

var evidence = Path.Combine(Path.GetTempPath(), "agv-charging-return-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);

// The default flow completes at the safe exit so the vehicle can immediately accept a call.
var direct = new Fixture(evidence, "direct-exit");
await direct.ToChargingAsync();
direct.Battery(80, true, 3);
await direct.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
direct.Battery(80, false, -2);
await direct.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
Check(direct.Port.Navigations.Select(n => n.Command.Split('-')[^1]).SequenceEqual(["IN", "OUT"]),
    "relay exit is driven only after the stop is confirmed by stationary and discharge evidence");
Check(direct.Port.Has(direct.Current.SessionId, FakePort.Bay), "the charging bay is still held while the vehicle drives out");
direct.Arrive("EXIT");
await direct.UntilAsync(s => s.Phase == ChargingPhase.Completed);
Check(direct.Port.Navigations.Count == 2 && direct.Port.SessionLeaseCount(direct.Current.SessionId) == 0 &&
    direct.Port.ReleaseCount == 1 && direct.Service.AllowsNewBusiness("A"),
    "safe exit completes charging, releases every resource and restores dispatch eligibility without a standby trip");

// A commissioned site can still request the legacy return workflow explicitly.
var full = new Fixture(evidence, "explicit-return", returnAfterCharging: true);
await full.ToChargingAsync();
full.Battery(80, true, 3);
await full.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
full.Battery(80, false, -2);
await full.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
full.Arrive("EXIT");
await full.UntilAsync(s => s.Phase == ChargingPhase.Returning);
var returnNavigation = full.Port.Navigations[^1];
Check(returnNavigation.Command.EndsWith("-RETURN", StringComparison.Ordinal) &&
    returnNavigation.Path.SequenceEqual(FakePort.ReturnRoute),
    "an explicitly configured return sends one navigation over the planned return road");
Check(!full.Port.Has(full.Current.SessionId, FakePort.Bay) &&
    full.Port.Has(full.Current.SessionId, FakePort.Back) && full.Port.ReleaseCount == 0,
    "an explicitly configured return releases the bay at the exit and keeps only the return road");
full.Battery(80, false, 0);
full.Arrive("STANDBY");
await full.UntilAsync(s => s.Phase == ChargingPhase.Completed);
Check(full.Port.SessionLeaseCount(full.Current.SessionId) == 0 &&
    full.Port.ReleaseCount == 1 && full.Service.AllowsNewBusiness("A"),
    "the optional return completes at its configured standby point");

// C10: an occupied exit found when the stop is confirmed waits for the road without Hold and keeps the bay.
var exitBusy = new Fixture(evidence, "exit-occupied");
await exitBusy.ToChargingAsync();
exitBusy.Port.ExitOccupied = true;
exitBusy.Battery(80, true, 3);
await exitBusy.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
exitBusy.Battery(80, false, 0);
await exitBusy.CyclesAsync(3);
Check(exitBusy.Current.Phase == ChargingPhase.AwaitingStop && exitBusy.Port.Navigations.Count == 1 &&
    exitBusy.Port.Has(exitBusy.Current.SessionId, FakePort.Bay),
    "an occupied exit at stop time waits in AwaitingStop with the bay retained and no exit navigation");
exitBusy.Port.ExitOccupied = false;
await exitBusy.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
Check(exitBusy.Port.Navigations.Count == 2, "the waiting vehicle drives out once the exit is physically free");

// C1: the return road is held before the vehicle leaves the exit; a busy return road waits at the exit, not in Hold.
var returnBusy = new Fixture(evidence, "return-busy", returnAfterCharging: true);
await returnBusy.ToChargingAsync();
returnBusy.Battery(80, true, 3);
await returnBusy.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
returnBusy.Battery(80, false, -2);
await returnBusy.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
returnBusy.Port.ReturnBlocked = true;
returnBusy.Arrive("EXIT");
await returnBusy.CyclesAsync(3);
Check(returnBusy.Current.Phase == ChargingPhase.Exiting && returnBusy.Port.Navigations.Count == 2 &&
    !returnBusy.Port.Has(returnBusy.Current.SessionId, FakePort.Bay) && returnBusy.Port.Has(returnBusy.Current.SessionId, FakePort.Out),
    "a busy return road keeps the vehicle at the exit with its exit lease, without Hold or a return navigation");
returnBusy.Port.ReturnBlocked = false;
await returnBusy.UntilAsync(s => s.Phase == ChargingPhase.Returning);
Check(returnBusy.Port.Navigations.Count == 3, "the return navigation is sent once the return road is free");

// C7: a reservation that cannot be proven again before the navigation write returns the session to the queue; no command is sent.
var phantom = new Fixture(evidence, "phantom-reservation");
phantom.Port.RefuseReserveCall = 2;
await phantom.CyclesAsync(1);
Check(phantom.Current.Phase == ChargingPhase.Queued && phantom.Current.Site is null && phantom.Current.Route is null &&
    phantom.Port.Navigations.Count == 0 && phantom.Port.SessionLeaseCount(phantom.Current.SessionId) == 0,
    "a reservation that is refused on the re-check falls back to the queue with no bay, no route and no navigation");
phantom.Port.RefuseReserveCall = null;
await phantom.UntilAsync(s => s.Phase == ChargingPhase.GoingToCharger);
Check(phantom.Port.Navigations.Count == 1, "the queued vehicle is reserved again and navigates once the bay is provably free");

// C2: one failed observation is counted; the session holds only after the consecutive limit, and resumes when observation clears.
var transient = new Fixture(evidence, "transient-validation");
await transient.ToChargingAsync();
transient.Port.ValidationError = "preflight rejected once";
await transient.CyclesAsync(1);
Check(transient.Current.Phase == ChargingPhase.Charging && transient.Current.TransientFailures == 1 && transient.Port.LeaseCount == 1,
    "one rejected map preflight is counted without moving the charge to Hold");
transient.Port.ValidationError = null;
await transient.CyclesAsync(1);
Check(transient.Current.Phase == ChargingPhase.Charging && transient.Current.TransientFailures == 0,
    "a clear observation resets the consecutive failure count");

var held = new Fixture(evidence, "held-telemetry");
await held.ToChargingAsync();
held.Robot.ReadFailure = new IOException("telemetry lost");
await held.CyclesAsync(held.Options.TransientFailureLimit);
Check(held.Current.Phase == ChargingPhase.Hold && held.Current.TransientHold && held.Port.LeaseCount == 1 &&
    held.Robot.RelayWrites.SequenceEqual([true]), "consecutive telemetry failures hold the session with the bay still reserved");
held.Robot.ReadFailure = null;
await held.CyclesAsync(2);
Check(held.Current.Phase == ChargingPhase.Charging && held.Robot.RelayWrites.SequenceEqual([true]),
    "the held charge resumes by itself when telemetry is readable again, with no relay write");

var overHeated = new Fixture(evidence, "held-over-temperature");
await overHeated.ToChargingAsync();
overHeated.Robot.ReadFailure = new IOException("telemetry lost");
await overHeated.CyclesAsync(overHeated.Options.TransientFailureLimit);
overHeated.Robot.ReadFailure = null;
overHeated.Battery(50, true, 4, overHeated.Options.MaximumTemperature + 1);
await overHeated.CyclesAsync(4);
Check(overHeated.Current.Phase == ChargingPhase.Hold && overHeated.Robot.RelayWrites.SequenceEqual([true, false]) && overHeated.Port.LeaseCount == 1,
    "over-temperature while held allows exactly one relay-off and then waits for review");
await overHeated.CyclesAsync(3);
Check(overHeated.Robot.RelayWrites.SequenceEqual([true, false]), "the held over-temperature stop is never repeated");

var targetHeld = new Fixture(evidence, "held-target");
await targetHeld.ToChargingAsync();
targetHeld.Robot.ReadFailure = new IOException("telemetry lost");
await targetHeld.CyclesAsync(targetHeld.Options.TransientFailureLimit);
targetHeld.Robot.ReadFailure = null;
targetHeld.Battery(80, true, 3);
await targetHeld.CyclesAsync(4);
Check(targetHeld.Robot.RelayWrites.SequenceEqual([true, false]) && targetHeld.Port.LeaseCount == 1 &&
    targetHeld.Current.Phase is ChargingPhase.AwaitingStop or ChargingPhase.Exiting or ChargingPhase.Hold,
    "a target reached while held relays off exactly once and keeps the bay until the stop is confirmed");

// C14: a restart during observation resumes without a relay write and without Hold.
var restarted = new Fixture(evidence, "restart-observation");
await restarted.ToChargingAsync();
restarted.Battery(80, true, 3);
await restarted.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
restarted.Restart();
await restarted.CyclesAsync(1);
Check(restarted.Current.Phase == ChargingPhase.AwaitingStop && restarted.Robot.RelayWrites.SequenceEqual([true, false]) &&
    restarted.Port.LeaseCount == 1, "a restart in AwaitingStop resumes observation without replaying the relay");
restarted.Battery(80, false, 0);
await restarted.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
Check(restarted.Port.Navigations.Count == 2, "after the restart the confirmed stop still leads to the normal exit");

// C9: a bay that gave no current is in cooldown for other vehicles and raises an alarm; the vehicle still leaves by the exit.
var faulted = new Fixture(evidence, "bay-fault-cooldown");
await faulted.ToAwaitingCurrentAsync();
faulted.Battery(20, false, 0);
faulted.Clock.Advance(TimeSpan.FromSeconds(faulted.Options.CurrentTimeoutSeconds + 1));
await faulted.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
Check(faulted.Robot.RelayWrites.SequenceEqual([true, false]) && faulted.Current.BayFaultAt is not null,
    "a bay with no current is switched off once and marked for cooldown before the vehicle leaves");
Check(faulted.Service.AlarmCandidates(faulted.Clock.GetUtcNow()).Any(c => c.Code == "CHARGING_BAY_FAULT"),
    "the bay fault raises a charging alarm candidate while the cooldown lasts");
faulted.Arrive("EXIT");
await faulted.UntilAsync(s => s.Phase == ChargingPhase.Completed);
faulted.Port.Put("B", 10);
await faulted.CyclesAsync(2);
Check(faulted.Status.Sessions.Last(s => s.VehicleId == "B").HasReservation == false,
    "another vehicle is not given the faulted bay during its cooldown");
faulted.Clock.Advance(TimeSpan.FromMinutes(faulted.Options.BayFaultCooldownMinutes + 1));
await faulted.CyclesAsync(2);
Check(faulted.Status.Sessions.Last(s => s.VehicleId == "B").HasReservation &&
    !faulted.Service.AlarmCandidates(faulted.Clock.GetUtcNow()).Any(c => c.Code == "CHARGING_BAY_FAULT"),
    "after the cooldown the bay is offered again and the alarm clears");

// C11: configuration gaps are rejected before any vehicle is sent.
Check(new ChargingOptions { TargetBatteryPercent = 30 }.ConfigurationError() is not null, "a start and target gap below ten points is rejected");
Check(new ChargingOptions { StartBatteryPercent = 18, CriticalBatteryPercent = 15 }.ConfigurationError() is not null,
    "a start threshold below the dispatch minimum battery is rejected");
Check(new ChargingOptions { Sites = [new("BAY-1", "CHARGER-1", "EXIT", "BAY-1") { Commissioned = true, AllowedVehicleIds = ["A"] },
    new("BAY-2", "CHARGER-2", "EXIT", "BAY-2") { Commissioned = true, AllowedVehicleIds = ["B"] }] }.ConfigurationError() is not null,
    "two bays that share one exit are rejected");

// C15: a current that has tapered near full is a normal stop, and a charge that gains nothing for the stall window is held.
var nearFull = new Fixture(evidence, "near-full-taper", target: 100);
await nearFull.ToChargingAsync();
nearFull.Battery(96, true, 0);
await nearFull.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
Check(nearFull.Robot.RelayWrites.SequenceEqual([true, false]), "a tapered current near full is a normal stop, not a fault hold");
var stall = new Fixture(evidence, "charge-stall");
await stall.ToChargingAsync();
await stall.CyclesAsync(1);
stall.Clock.Advance(TimeSpan.FromMinutes(stall.Options.ChargeStallMinutes + 1));
await stall.CyclesAsync(1);
Check(stall.Current.Phase == ChargingPhase.Hold && stall.Robot.RelayWrites.SequenceEqual([true, false]),
    "a charge that gains nothing for the stall window is relayed off once and held for review");

// C16: a vehicle that does not report temperature is exempt by configuration; otherwise the gap stops the charge.
var exempt = new Fixture(evidence, "temperature-exempt", optionalTemperature: true);
await exempt.ToChargingAsync();
exempt.Battery(30, true, 4);
exempt.Robot.Battery = exempt.Robot.Battery with { Temperature = null };
await exempt.CyclesAsync(3);
Check(exempt.Current.Phase == ChargingPhase.Charging && exempt.Robot.RelayWrites.SequenceEqual([true]),
    "a vehicle exempt from temperature keeps charging without a temperature signal");
var required = new Fixture(evidence, "temperature-required");
await required.ToChargingAsync();
required.Battery(30, true, 4);
required.Robot.Battery = required.Robot.Battery with { Temperature = null };
await required.CyclesAsync(1);
Check(required.Current.Phase == ChargingPhase.Hold && required.Robot.RelayWrites.SequenceEqual([true, false]),
    "a vehicle that must report temperature is relayed off when the signal is missing");

Console.WriteLine($"PASS: {checks} relay exit and optional return checks.");

sealed class Fixture
{
    public FakeClock Clock { get; } = new();
    public FakePort Port { get; }
    public FakeRobot Robot { get; }
    public ChargingOptions Options { get; }
    public ChargingService Service { get; private set; }
    private readonly string path;
    public ChargingStatus Status => Service.ReadAsync(default).GetAwaiter().GetResult();
    public ChargingSession Current => Status.Sessions.Last(s => s.VehicleId == "A");
    public Fixture(string root, string name, decimal target = 80, bool optionalTemperature = false,
        bool returnAfterCharging = false)
    {
        path = Path.Combine(root, name + ".json");
        Port = new(Clock); Robot = new(Clock);
        Port.ReturnAfterCharging = returnAfterCharging;
        Options = new() { Enabled = true, Sites = [new("BAY-1", "CHARGER", "EXIT", FakePort.Bay)
            { Commissioned = true, AllowedVehicleIds = ["A", "B"] }], DefaultStandbySiteId = "STANDBY",
            TargetBatteryPercent = target, TemperatureOptionalVehicleIds = optionalTemperature ? ["A"] : [] };
        Port.Put("A", 20);
        Robot.Battery = new("A", 20, false, 0, 25, false, false, Clock.GetUtcNow());
        Service = NewService();
    }
    private ChargingService NewService() => new(Options, Port, Robot, path, NullLogger<ChargingService>.Instance, Clock);
    public void Battery(decimal percent, bool charging, double current, double temperature = 25)
    {
        Robot.Battery = new("A", percent, charging, current, temperature, false, false, Clock.GetUtcNow());
        Port.Put("A", percent, charging, current, temperature);
    }
    public void Restart() => Service = NewService();
    public void Arrive(string site)
    {
        Port.Sites["A"] = site;
        Port.Completed.UnionWith(Port.Navigations.Where(n => n.Vehicle == "A").Select(n => n.Command));
    }
    public async Task CyclesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Port.Refresh();
            Robot.Battery = Robot.Battery with { ObservedAt = Clock.GetUtcNow() };
            await Service.RunCycleAsync(default);
        }
    }
    public async Task UntilAsync(Func<ChargingSession, bool> predicate)
    {
        for (var i = 0; i < 16; i++)
        {
            await CyclesAsync(1);
            if (Status.Sessions.LastOrDefault(s => s.VehicleId == "A") is { } s && predicate(s)) return;
        }
        throw new InvalidOperationException("Expected charging phase not reached; last session: " + Current);
    }
    public async Task ToAwaitingCurrentAsync()
    {
        await UntilAsync(s => s.Phase == ChargingPhase.GoingToCharger);
        Arrive("CHARGER");
        await UntilAsync(s => s.Phase == ChargingPhase.AwaitingCurrent);
    }
    public async Task ToChargingAsync()
    {
        await ToAwaitingCurrentAsync();
        Battery(20, true, 4);
        await UntilAsync(s => s.Phase == ChargingPhase.Charging);
    }
}

sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan delta) => now += delta;
}

sealed class FakeRobot(FakeClock clock) : IChargingRobotControl
{
    public ChargingBatteryReading Battery { get; set; } = new("A", 20, false, 0, 25, false, false, clock.GetUtcNow());
    public List<bool> RelayWrites { get; } = [];
    public Exception? ReadFailure { get; set; }
    public Task SetChargingRelayAsync(string vehicleId, bool enabled, CancellationToken ct)
    {
        RelayWrites.Add(enabled);
        return Task.CompletedTask;
    }
    public Task<ChargingBatteryReading> ReadBatteryAsync(string vehicleId, CancellationToken ct)
    {
        if (ReadFailure is not null) throw ReadFailure;
        return Task.FromResult(Battery with { VehicleId = vehicleId });
    }
}

// Physical lease model: each leg of the charging route owns its own step, as in the production traffic manager.
sealed class FakePort(FakeClock clock) : IChargingRuntimePort
{
    public const string Bay = "CHARGE-BAY", In = "ROAD-IN", Out = "ROAD-OUT", Back = "ROAD-RETURN";
    public static readonly string[] ReturnRoute = ["EXIT", "STANDBY"];
    private readonly Dictionary<string, AgvRobotSnapshot> vehicles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Step, string Session, string Resource)> leases = [];
    public Dictionary<string, string> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Completed { get; } = [];
    public List<(string Command, string Vehicle, string[] Path)> Navigations { get; } = [];
    public bool ExitOccupied { get; set; }
    public bool ReturnBlocked { get; set; }
    public bool ReturnAfterCharging { get; set; }
    public int ReserveCalls { get; private set; }
    public int? RefuseReserveCall { get; set; }
    public int ReleaseCount { get; private set; }
    public IReadOnlyList<AgvRobotSnapshot> Vehicles => vehicles.Values.ToArray();
    public string? NewWorkBlockReason => null;
    public bool Has(string session, string resource) => leases.Any(l => l.Session == session && l.Resource == resource);
    public int SessionLeaseCount(string session) => leases.Count(l => l.Session == session);
    public int LeaseCount => leases.Count;
    public void Put(string id, decimal percent, bool charging = false, double current = 0, double temperature = 25)
    {
        Sites.TryAdd(id, "HOME");
        vehicles[id] = new(id, id, "127.0.0.1", 19204, ConnectionState.Online, SignalQuality.Good,
            0, 0, 0, 1, Sites[id], null, percent, false, false, "Idle", null, null, null, [], [],
            clock.GetUtcNow(), clock.GetUtcNow(), 0, 1, null, 1)
        { VelocityX = 0, VelocityY = 0, AngularVelocity = 0, Charging = charging,
            BatteryCurrent = current, BatteryTemperature = temperature };
    }
    public void Refresh()
    {
        foreach (var id in vehicles.Keys.ToArray())
            vehicles[id] = vehicles[id] with { CurrentStation = Sites[id], LastSuccessfulPollAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
    }
    private bool Take(string step, string session, string[] resources)
    {
        if (resources.Any(r => leases.Any(l => l.Resource == r && l.Session != session))) return false;
        foreach (var resource in resources)
            if (!leases.Any(l => l.Step == step && l.Resource == resource)) leases.Add((step, session, resource));
        return true;
    }
    private int Trim(string step, string[] keep)
    {
        var removed = leases.RemoveAll(l => l.Step == step && !keep.Contains(l.Resource));
        return removed;
    }
    public ValueTask<IDisposable?> EnterExecutionAsync(bool newWork, CancellationToken ct) =>
        ValueTask.FromResult<IDisposable?>(new Lease());
    public ValueTask<IDisposable?> EnterRecoveryControlAsync(CancellationToken ct) => ValueTask.FromResult<IDisposable?>(new Lease());
    public Task<string?> BusyReasonAsync(string vehicleId, string? ownSessionId, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<ChargingRoute?> PlanAsync(string vehicleId, ChargingSite site, CancellationToken ct) =>
        PlanWithReturnAsync(vehicleId, site, null, ct);
    public Task<ChargingRoute?> PlanWithReturnAsync(string vehicleId, ChargingSite site, string? returnSiteId, CancellationToken ct)
    {
        var resources = ReturnAfterCharging ? new[] { In, Bay, Out, Back } : new[] { In, Bay, Out };
        return Task.FromResult<ChargingRoute?>(
            new("SYNTHETIC-MAP", "V1", [Sites[vehicleId], site.ChargingSiteId],
                [site.ChargingSiteId, site.ExitSiteId], resources, 5)
            {
                OriginSiteId = Sites[vehicleId],
                ReturnSiteId = ReturnAfterCharging ? "STANDBY" : null,
                ReturnPath = ReturnAfterCharging ? ReturnRoute : []
            });
    }
    public string? ValidationError { get; set; }
    public Task<string?> ValidateAsync(ChargingSession session, bool requireStopped, CancellationToken ct) => Task.FromResult(ValidationError);
    public bool IsAt(string vehicleId, string siteId) => Sites.GetValueOrDefault(vehicleId) == siteId;
    public Task<bool> ReserveAsync(ChargingSession session, CancellationToken ct)
    {
        ReserveCalls++;
        return Task.FromResult(ReserveCalls != RefuseReserveCall && Take(session.SessionId + "-LOCK", session.SessionId, [In, Bay]));
    }
    public Task<int> ShrinkToBayAsync(ChargingSession session, CancellationToken ct) =>
        Task.FromResult(Trim(session.SessionId + "-LOCK", [Bay]));
    public Task<bool> AcquireExitAsync(ChargingSession session, CancellationToken ct) =>
        Task.FromResult(!ExitOccupied && Take(session.SessionId + "-EXIT", session.SessionId, [Bay, Out]));
    public Task<bool> AcquireReturnAsync(ChargingSession session, CancellationToken ct)
    {
        if (ReturnBlocked || !Take(session.SessionId + "-RETURN", session.SessionId, [Back])) return Task.FromResult(false);
        Trim(session.SessionId + "-EXIT", [Back]);
        return Task.FromResult(true);
    }
    public Task<int> ReleaseBayAtExitAsync(ChargingSession session, CancellationToken ct)
    {
        var released = Trim(session.SessionId + "-LOCK", []);
        released += Trim(session.SessionId + "-EXIT", [Out]);
        return Task.FromResult(released);
    }
    public Task RenewAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseAsync(string sessionId, CancellationToken ct)
    {
        leases.RemoveAll(l => l.Session == sessionId);
        ReleaseCount++;
        return Task.CompletedTask;
    }
    public Task<string> NavigateAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct)
    {
        Navigations.Add((commandId, vehicleId, path.ToArray()));
        return Task.FromResult(commandId);
    }
    public Task<AgvTransportTaskStatus?> ObserveNavigationAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct) =>
        Task.FromResult<AgvTransportTaskStatus?>(new(commandId, commandId,
            Completed.Contains(commandId) ? AgvTransportTaskState.Completed : AgvTransportTaskState.GoingToDestination, vehicleId, clock.GetUtcNow()));
    private sealed class Lease : IDisposable { public void Dispose() { } }
}
