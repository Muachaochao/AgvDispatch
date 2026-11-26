using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
var admin = TrafficUserContext.LocalOperation("charging-smoke-test");
var evidence = Path.Combine(Path.GetTempPath(), "agv-charging-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);

var monitorAck = new TcpListener(IPAddress.Loopback, 0);
var monitorStatus = new TcpListener(IPAddress.Loopback, 0);
monitorAck.Start(); monitorStatus.Start();
var monitor = new ChargerMonitoringService(new ChargerMonitoringOptions
{
    Addresses = ["127.0.0.1"],
    AckPort = ((IPEndPoint)monitorAck.LocalEndpoint).Port,
    StatusPort = ((IPEndPoint)monitorStatus.LocalEndpoint).Port,
    PollIntervalMs = 1000,
    ConnectTimeoutMs = 500
}, NullLogger<ChargerMonitoringService>.Instance);
await monitor.StartAsync(default);
for (var i = 0; i < 20 && monitor.Read().Count == 0; i++) await Task.Delay(100);
Check(monitor.Read().Single() is { AckPortReachable: true, StatusPortReachable: true, Outcome: "connected" },
    "charger monitor checks both ports without sending control payload");
monitorStatus.Stop();
await Task.Delay(1500);
Check(monitor.Read().Single() is { AckPortReachable: true, StatusPortReachable: false, Outcome: "degraded" },
    "charger monitor reports partial communication loss");
await monitor.StopAsync(default);
monitor.Dispose(); monitorAck.Stop();
Console.WriteLine("Isolated evidence: " + evidence);

Check(new ChargingOptions().ConfigurationError() is null, "default charging thresholds are valid");
Check(new ChargingOptions { StartBatteryPercent = 80, TargetBatteryPercent = 80 }.ConfigurationError() is not null,
    "equal start and target thresholds are rejected");
Check(new ChargingOptions { CriticalBatteryPercent = 30 }.ConfigurationError() is not null,
    "critical threshold cannot exceed the charge request threshold");
Check(new ChargingOptions { MinimumChargingCurrent = double.NaN }.ConfigurationError() is not null,
    "nonfinite charge current is rejected");

// These fixtures drive physical evidence separately from accepted API calls. No robot,
// PLC, operating database, or daily service is contacted by this executable.
var normal = new Fixture(evidence, "normal");
await normal.ToChargingAsync();
Check(normal.Robot.RelayWrites.SequenceEqual([true]), "start ACK alone is followed by explicit charging-current observation");
Check(normal.Port.IntentPersistedBeforeEveryNavigation, "navigation identity is durably present before each physical send");
normal.Battery(40, true, 3);
await normal.CyclesAsync(4);
Check(normal.Current.Phase == ChargingPhase.Charging && normal.Port.Leases.Count == 1 && !normal.Service.AllowsNewBusiness("A"),
    "40 percent retains the charging session and resource until the configured target");
normal.Battery(80, true, 3);
await normal.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
Check(normal.Robot.RelayWrites.SequenceEqual([true, false]) && normal.Port.Navigations.Count == 1,
    "target battery requests stop but cannot exit before stop evidence");
normal.Battery(80, false, 2);
await normal.CyclesAsync(2);
Check(normal.Current.Phase == ChargingPhase.AwaitingStop && normal.Port.Navigations.Count == 1,
    "positive charging current prevents exit even when the Charging flag is false");
normal.Battery(80, false, -2);
await normal.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
Check(normal.Current.Phase == ChargingPhase.Exiting,
    "commissioned negative discharge current with Charging false confirms charging has stopped");
Check(normal.Port.Leases.Count == 1, "charge bay remains reserved during vehicle exit");
normal.Arrive("EXIT");
await normal.UntilAsync(s => s.Phase == ChargingPhase.Completed);
Check(normal.Port.Leases.Count == 0 && normal.Port.ReleaseCount == 1 && normal.Service.AllowsNewBusiness("A"),
    "only completed exit with physical arrival releases the bay and vehicle");
normal.Battery(35, false, 0);
await normal.CyclesAsync(4);
Check(normal.Status.Sessions.Count == 1 && normal.Port.Navigations.Count == 2,
    "completed vehicle above the start threshold does not immediately recharge");

var queue = new Fixture(evidence, "queue");
queue.Port.Put("A", 24);
queue.Port.Put("B", 10);
await queue.CyclesAsync(8);
Check(queue.Port.Navigations.Count == 1 && queue.Port.Navigations[0].Vehicle == "B" && queue.Port.Leases.Count == 1,
    "two vehicles competing for one bay dispatch only the lowest battery vehicle");
Check(!queue.Service.AllowsNewBusiness("B") && !queue.Service.AllowsNewBusiness("A"),
    "a vehicle below the charging threshold cannot take new work even while the bay is occupied");
Check(queue.Status.Sessions.Any(s => s.VehicleId == "A" && s.Phase == ChargingPhase.Queued),
    "a low battery vehicle is visible in the queue while the only bay is occupied");

var doubleBay = new Fixture(evidence, "double-bay-queue");
doubleBay.Options.Sites =
[
    doubleBay.Options.Sites[0] with { AllowedVehicleIds = ["A", "B", "C"] },
    new("BAY-2", "CHARGER-2", "EXIT-2", "CHARGE-RESOURCE-2")
        { Commissioned = true, AllowedVehicleIds = ["A", "B", "C"] }
];
doubleBay.Options.MaximumVehiclesOutOfService = 2;
doubleBay.Port.Put("A", 18); doubleBay.Port.Put("B", 20); doubleBay.Port.Put("C", 22);
await doubleBay.CyclesAsync(3);
Check(doubleBay.Port.Navigations.Select(n => n.Vehicle).SequenceEqual(["A", "B"]) &&
    doubleBay.Status.Sessions.Any(s => s.VehicleId == "C" && s.Phase == ChargingPhase.Queued &&
        s.Message.Contains("BAY-1：已被其他充电会话预留") && s.Message.Contains("BAY-2：已被其他充电会话预留")) &&
    !doubleBay.Service.AllowsNewBusiness("C"),
    "two lower battery vehicles reserve distinct bays while the third remains queued with no movement or new work");

var busy = new Fixture(evidence, "busy");
busy.Port.Busy.Add("A");
await busy.CyclesAsync(4);
Check(busy.Port.Navigations.Count == 0 && busy.Robot.RelayWrites.Count == 0,
    "unfinished business or equipment responsibility prevents charging motion");
var parking = new MapPoint2D[] { new(-2,-2), new(2,-2), new(2,2), new(-2,2) };
Check(ChargingWaitingGeometry.ContainsBody(new(0,0),1,parking) &&
    !ChargingWaitingGeometry.ContainsBody(new(0,1.5),1,parking) &&
    !ChargingWaitingGeometry.ContainsBody(new(3,0),1,parking),
    "a staging bay accepts only a body envelope fully inside its parking boundary");
var staging = new Fixture(evidence, "safe-waiting");
staging.Options.WaitingSiteIds = ["SAFE"];
staging.Options.Sites = [staging.Options.Sites[0] with { AllowedVehicleIds = ["A", "B", "C"] },
    new("BAY-2", "CHARGER-2", "EXIT-2", "CHARGE-RESOURCE-2") { Commissioned = true, AllowedVehicleIds = ["A", "B", "C"] }];
staging.Port.Put("C", 22);
staging.Port.WaitingRoute = new("SYNTHETIC-MAP", "V1", ["HOME", "SAFE"], "SAFE-RESOURCE", ["ROAD", "SAFE-RESOURCE"], 5);
staging.Port.Put("B", 24);
staging.Port.Busy.Add("B");
await staging.CyclesAsync(3);
Check(staging.Port.Navigations.Count == 2, "unfinished business prevents charging staging");
staging.Port.Busy.Clear();
await staging.CyclesAsync(2);
var staged = staging.Status.Sessions.Single(x => x.VehicleId == "B");
Check(staged.Phase == ChargingPhase.GoingToWaitingSite && staging.Port.Navigations.Count == 3 &&
    staging.Port.IntentPersistedBeforeEveryNavigation, "a full bay sends the waiting vehicle once after a durable intent");
staging.Restart();
await staging.CyclesAsync(1);
Check(staging.Port.Navigations.Count == 3, "restart observes the original waiting navigation without resending");
staging.Port.Completed.Add(staged.CommandId!);
await staging.CyclesAsync(1);
Check(staging.Port.Leases.ContainsKey("ROAD"), "terminal receipt without physical arrival retains the waiting approach");
staging.Port.Sites["B"] = "SAFE";
await staging.CyclesAsync(2);
Check(!staging.Port.Leases.ContainsKey("ROAD") && staging.Port.Leases.ContainsKey("SAFE-RESOURCE") &&
    !staging.Service.AllowsNewBusiness("B"), "physical waiting arrival releases the approach and keeps the parking lease");

var eta = new Fixture(evidence, "bay-eta");
eta.Options.Sites = [eta.Options.Sites[0], new("BAY-2", "CHARGER-2", "EXIT-2", "CHARGE-RESOURCE-2")
    { Commissioned = true, AllowedVehicleIds = ["A", "B"] }];
eta.Port.ApproachEstimates["BAY-1"] = new(5, 60);
eta.Port.ApproachEstimates["BAY-2"] = new(20, 0);
await eta.CyclesAsync(1);
Check(eta.Current.Site?.Id == "BAY-2", "approach travel and traffic delay determine the earliest charging option");

var nearRelease = new Fixture(evidence, "near-release");
nearRelease.Options.EstimatedGainPercentPerMinute = 10;
nearRelease.Options.EstimatedDockExitSeconds = 10;
await nearRelease.ToChargingAsync();
nearRelease.Battery(79, true, 4);
await nearRelease.CyclesAsync(1);
nearRelease.Options.Sites = [nearRelease.Options.Sites[0], new("BAY-2", "CHARGER-2", "EXIT-2", "CHARGE-RESOURCE-2")
    { Commissioned = true, AllowedVehicleIds = ["A", "B"] }];
nearRelease.Port.ApproachEstimates["BAY-1"] = new(5, 20);
nearRelease.Port.ApproachEstimates["BAY-2"] = new(50, 0);
nearRelease.Port.Put("B", 24);
await nearRelease.CyclesAsync(1);
Check(nearRelease.Status.Sessions.Single(session => session.VehicleId == "B") is { Phase: ChargingPhase.Queued } queueRow &&
    queueRow.Message.StartsWith("等空桩") && queueRow.SiteEstimates.Any(estimate => estimate.SiteId == "BAY-1" && estimate.StartInSeconds == 25),
    "an occupied nearby bay is preferred only by its predicted release and traffic time; no navigation is sent before the grant");
nearRelease.Clock.Advance(TimeSpan.FromSeconds(26));
nearRelease.Battery(80, false, 0);
await nearRelease.CyclesAsync(1);
Check(nearRelease.Status.Sessions.Single(session => session.VehicleId == "B").Site?.Id == "BAY-2",
    "a stale or exceeded wait estimate does not indefinitely defer an executable bay");

// B07-FIX C8: a vehicle that falls below critical during its business cycle raises a critical charging alarm while it waits.
busy.Port.Put("A", 10);
busy.Port.Busy.Add("A");
await busy.CyclesAsync(2);
Check(busy.Service.AlarmCandidates(busy.Clock.GetUtcNow()).Any(c => c.Code == "CHARGING_CRITICAL_WAITING" && c.Severity == AlarmSeverity.Critical) &&
    !busy.Service.AllowsNewBusiness("A"), "a critical vehicle waiting for its business cycle raises a critical alarm and takes no new business");
busy.Port.Busy.Clear();
busy.Port.Put("A", 20);
await busy.UntilAsync(s => s.Phase == ChargingPhase.GoingToCharger);
Check(busy.Port.Navigations.Count == 1, "closing the current business cycle allows queued charging");

var docking = new Fixture(evidence, "docking-timeout");
await docking.UntilAsync(s => s.Phase == ChargingPhase.GoingToCharger);
docking.Clock.Advance(TimeSpan.FromSeconds(docking.Options.NavigationTimeoutSeconds + 1));
await docking.CyclesAsync(1);
Check(docking.Current.Phase == ChargingPhase.Hold && docking.Port.Leases.Count == 1 && docking.Robot.RelayWrites.Count == 0,
    "docking timeout retains the bay and never energizes the charging relay");

var movingDock = new Fixture(evidence, "moving-dock");
await movingDock.UntilAsync(s => s.Phase == ChargingPhase.GoingToCharger);
movingDock.Port.Stopped = false;
movingDock.Arrive("CHARGER");
await movingDock.CyclesAsync(2);
Check(movingDock.Current.Phase == ChargingPhase.Hold && movingDock.Robot.RelayWrites.Count == 0,
    "completed navigation and charger station label cannot energize a vehicle that is still moving");

var noCurrent = new Fixture(evidence, "no-current");
await noCurrent.ToAwaitingCurrentAsync();
noCurrent.Battery(20, true, 0);
await noCurrent.CyclesAsync(1);
Check(noCurrent.Current.Phase != ChargingPhase.Charging, "Charging flag without positive current does not prove charging started");
noCurrent.Battery(20, true, -2);
await noCurrent.CyclesAsync(1);
Check(noCurrent.Current.Phase == ChargingPhase.AwaitingCurrent,
    "negative discharge current cannot establish charging even when Charging is true");
noCurrent.Clock.Advance(TimeSpan.FromSeconds(noCurrent.Options.CurrentTimeoutSeconds + 1));
await noCurrent.CyclesAsync(1);
// B07-FIX C9 (deliberate change): a bay with no current after the start timer is a bay fault, not a manual review.
// It is switched off once, stays reserved, and the vehicle leaves by the normal exit after current is confirmed zero.
Check(noCurrent.Current.Phase == ChargingPhase.AwaitingStop && noCurrent.Current.BayFaultAt is not null && noCurrent.Port.Leases.Count == 1,
    "no-current timeout switches the relay off and keeps the bay reserved until the stop is confirmed");
Check(noCurrent.Robot.RelayWrites.SequenceEqual([true, false]),
    "no-current timeout makes one protective relay stop before the stop is confirmed");
noCurrent.Battery(20, false, 0);
await noCurrent.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
Check(noCurrent.Port.Navigations.Count == 2 && noCurrent.Port.Leases.Count == 1 && noCurrent.Robot.RelayWrites.SequenceEqual([true, false]),
    "after current is confirmed zero the vehicle leaves automatically and the bay stays until the exit");

var overTemperature = new Fixture(evidence, "over-temperature");
await overTemperature.ToChargingAsync();
overTemperature.Battery(80, true, 4, overTemperature.Options.MaximumTemperature + 1);
overTemperature.Port.ValidationError = "BMS overtemperature error alarm blocks navigation";
await overTemperature.CyclesAsync(1);
Check(overTemperature.Current.Phase == ChargingPhase.Hold && overTemperature.Port.Leases.Count == 1,
    "overtemperature with a BMS motion-blocking alarm at target battery requires protective hold");
Check(overTemperature.Robot.RelayWrites.Count(v => !v) == 1,
    "overtemperature requests one protective relay stop");
await overTemperature.CyclesAsync(3);
Check(overTemperature.Robot.RelayWrites.Count(v => !v) == 1,
    "held overtemperature never automatically repeats the stop command");

var taperedCurrent = new Fixture(evidence, "target-tapered-current");
await taperedCurrent.ToChargingAsync();
taperedCurrent.Battery(80, false, 0);
await taperedCurrent.UntilAsync(s => s.Phase == ChargingPhase.AwaitingStop);
Check(taperedCurrent.Current.Phase == ChargingPhase.AwaitingStop && taperedCurrent.Robot.RelayWrites.SequenceEqual([true, false]) && taperedCurrent.Port.Leases.Count == 1,
    "target battery with naturally tapered current follows normal stop confirmation instead of fault hold");

var disconnected = new Fixture(evidence, "disconnect");
await disconnected.ToChargingAsync();
disconnected.Robot.ReadFailure = new IOException("isolated simulated disconnect");
// B07-FIX C2 (deliberate change): one failed telemetry read is counted and the charge keeps its phase; the hold comes only
// after the consecutive failure limit. Either way the bay stays reserved and the vehicle stays out of business.
await disconnected.CyclesAsync(1);
Check(disconnected.Current.Phase == ChargingPhase.Charging && disconnected.Current.TransientFailures == 1 &&
    disconnected.Port.Leases.Count == 1 && !disconnected.Service.AllowsNewBusiness("A"),
    "a single failed telemetry read is counted without holding the charge or freeing the bay");
await disconnected.CyclesAsync(disconnected.Options.TransientFailureLimit);
Check(disconnected.Current.Phase == ChargingPhase.Hold && disconnected.Current.TransientHold && disconnected.Port.Leases.Count == 1 &&
    !disconnected.Service.AllowsNewBusiness("A") && disconnected.Robot.RelayWrites.SequenceEqual([true]),
    "consecutive telemetry failures hold the session while the bay stays reserved and the relay is not reopened");

var interrupted = new Fixture(evidence, "execution-gate-interruption");
await interrupted.ToChargingAsync();
interrupted.Port.ExecutionBlocked = true;
interrupted.Battery(80, true, 4);
await interrupted.CyclesAsync(3);
Check(interrupted.Port.Leases.Count == 1 && !interrupted.Service.AllowsNewBusiness("A") && interrupted.Port.ReleaseCount == 0 &&
    interrupted.Robot.RelayWrites.SequenceEqual([true]) && interrupted.Port.Navigations.Count == 1,
    "execution gate interruption preserves the charging bay and vehicle without sending or releasing resources");

var stale = new Fixture(evidence, "stale-battery");
await stale.ToChargingAsync();
stale.RefreshBatteryTimestamp = false;
stale.Clock.Advance(TimeSpan.FromSeconds(stale.Options.StateMaxAgeSeconds + 1));
// B07-FIX C2: stale evidence holds after the consecutive limit rather than on the first stale reading.
await stale.CyclesAsync(stale.Options.TransientFailureLimit);
Check(stale.Current.Phase == ChargingPhase.Hold && stale.Port.Leases.Count == 1,
    "fresh vehicle position cannot replace stale charging-current evidence");

var uncertain = new Fixture(evidence, "uncertain-send");
uncertain.Port.NavigationFailure = new TimeoutException("accepted or lost response is unknown");
await uncertain.UntilAsync(s => s.Phase == ChargingPhase.Hold);
var original = uncertain.Current;
Check(uncertain.Port.Navigations.Count == 1 && uncertain.Port.Leases.Count == 1 && original.CommandId is not null,
    "uncertain send retains original navigation identity and physical resources");
Check(uncertain.Port.IntentPersistedBeforeEveryNavigation, "lost send response cannot occur before the original intent is persisted");
await uncertain.CyclesAsync(5);
Check(uncertain.Port.Navigations.Count == 1, "unknown navigation result is never automatically resent");
uncertain.Restart();
await uncertain.CyclesAsync(3);
Check(uncertain.Current.Phase == ChargingPhase.Hold && uncertain.Current.CommandId == original.CommandId && uncertain.Port.Leases.Count == 1 && uncertain.Port.Navigations.Count == 1,
    "restart preserves held command identity, reservation, and the no-resend barrier");
var continuation = await uncertain.Service.ResolveHoldAsync(admin,
    new(uncertain.Current.SessionId, ChargingHoldDecision.ContinueOriginal, "review original navigation receipt", true, true), default);
await uncertain.CyclesAsync(2);
Check(continuation.Accepted && uncertain.Port.Navigations.Count == 1,
    "operator continuation of an uncertain navigation only observes the original command");

var restart = new Fixture(evidence, "restart-active");
await restart.ToChargingAsync();
restart.Restart();
await restart.CyclesAsync(3);
// B07-FIX C14 (deliberate change): an observation-only charge resumes by itself after a restart and never replays the relay start.
Check(restart.Current.Phase == ChargingPhase.Charging && restart.Port.Leases.Count == 1 && restart.Robot.RelayWrites.Count == 1,
    "backend restart resumes an observed charge by itself without replaying the relay start");

var uncertainStart = new Fixture(evidence, "uncertain-start");
await uncertainStart.UntilAsync(s => s.Phase == ChargingPhase.GoingToCharger);
uncertainStart.Robot.StartFailure = new TimeoutException("relay start may have been accepted");
uncertainStart.Arrive("CHARGER");
await uncertainStart.UntilAsync(s => s.Phase == ChargingPhase.Hold);
uncertainStart.Restart();
await uncertainStart.CyclesAsync(3);
Check(uncertainStart.Current.StartAttempted && uncertainStart.Port.Leases.Count == 1 && uncertainStart.Robot.RelayWrites.SequenceEqual([true]),
    "unknown relay start result is retained across restart and never energized a second time");

var uncertainStop = new Fixture(evidence, "uncertain-stop");
await uncertainStop.ToChargingAsync();
uncertainStop.Robot.StopFailure = new TimeoutException("stop may have been accepted");
uncertainStop.Battery(80, true, 4);
await uncertainStop.UntilAsync(s => s.Phase == ChargingPhase.Hold);
await uncertainStop.CyclesAsync(4);
uncertainStop.Restart();
await uncertainStop.CyclesAsync(2);
Check(uncertainStop.Port.Leases.Count == 1 && uncertainStop.Current.StopAttempted && uncertainStop.Robot.RelayWrites.SequenceEqual([true, false]),
    "unknown relay stop result remains held across restart and is never repeated");

var manual = new Fixture(evidence, "manual-stop");
await manual.ToChargingAsync();
var stopped = await manual.Service.RequestStopAsync(admin, new(manual.Current.SessionId, "manual charging smoke intervention"), default);
Check(stopped.Accepted, "manual intervention records a stop request");
Check(manual.Current.Phase == ChargingPhase.Hold && manual.Port.Leases.Count == 1,
    "manual stop request keeps the occupied bay held for explicit review");
manual.Battery(45, false, 0);
await manual.CyclesAsync(3);
Check(manual.Current.Phase == ChargingPhase.Hold && manual.Port.Navigations.Count == 1 && manual.Port.ReleaseCount == 0,
    "manual stop confirmation alone cannot automatically depart or release the bay");
var reviewedStop = await manual.Service.ResolveHoldAsync(admin,
    new(manual.Current.SessionId, ChargingHoldDecision.ContinueOriginal, "charger safely stopped; continue original stop observation", true, true), default);
Check(reviewedStop.Accepted && manual.Current.Phase == ChargingPhase.AwaitingStop,
    "operator review resumes original stop observation without repeating relay control");
await manual.UntilAsync(s => s.Phase == ChargingPhase.Exiting);
manual.Arrive("EXIT");
await manual.UntilAsync(s => s.Phase == ChargingPhase.Completed);
Check(manual.Port.ReleaseCount == 1, "manual stop follows stop confirmation and physical exit before release");

var bootStop = new Fixture(evidence, "boot-hold-safety-stop");
await bootStop.ToChargingAsync();
bootStop.Restart();
bootStop.Port.ExecutionBlocked = true;
var bootSafetyStop = await bootStop.Service.RequestStopAsync(admin,
    new(bootStop.Current.SessionId, "explicit safety stop while startup recovery gate remains closed"), default);
await bootStop.CyclesAsync(3);
Check(bootSafetyStop.Accepted && bootStop.Port.RecoveryControlAcquisitions == 1 && bootStop.Current.Phase == ChargingPhase.Hold &&
    bootStop.Robot.RelayWrites.SequenceEqual([true, false]) && bootStop.Port.Leases.Count == 1 && bootStop.Port.Navigations.Count == 1 && bootStop.Port.ReleaseCount == 0,
    "BootHold allows one explicit relay-off through recovery control while keeping bay and motion blocked");
var repeatedBootStop = await bootStop.Service.RequestStopAsync(admin,
    new(bootStop.Current.SessionId, "repeat original stop must remain rejected"), default);
Check(!repeatedBootStop.Accepted && bootStop.Robot.RelayWrites.SequenceEqual([true, false]) && bootStop.Port.RecoveryControlAcquisitions == 1,
    "a repeated BootHold stop is rejected without reacquiring control or sending relay-off again");

var review = new Fixture(evidence, "manual-review");
await review.ToChargingAsync();
// B07-FIX C14 (test setup): a restart no longer holds an observed charge, so the review state is reached by an operator stop.
await review.Service.RequestStopAsync(admin, new(review.Current.SessionId, "operator review of a stopped charge"), default);
var unconfirmed = await review.Service.ResolveHoldAsync(admin,
    new(review.Current.SessionId, ChargingHoldDecision.CloseAfterPhysicalExit, "not yet physically reviewed", false, false), default);
Check(!unconfirmed.Accepted && review.Port.Leases.Count == 1, "manual close without physical confirmations cannot release charge resources");
var stillDocked = await review.Service.ResolveHoldAsync(admin,
    new(review.Current.SessionId, ChargingHoldDecision.CloseAfterPhysicalExit, "checkboxes alone do not prove exit", true, true), default);
Check(!stillDocked.Accepted && review.Port.Leases.Count == 1,
    "physical charge state and position override operator release checkboxes");
review.Battery(45, false, 0);
review.Arrive("EXIT");
review.Port.Stopped = false;
var movingRelease = await review.Service.ResolveHoldAsync(admin,
    new(review.Current.SessionId, ChargingHoldDecision.CloseAfterPhysicalExit, "vehicle is passing the exit", true, true), default);
Check(!movingRelease.Accepted && review.Port.Leases.Count == 1,
    "manual close cannot release a vehicle still moving through the exit station");
review.Port.Stopped = true;
var physicallyReleased = await review.Service.ResolveHoldAsync(admin,
    new(review.Current.SessionId, ChargingHoldDecision.CloseAfterPhysicalExit, "vehicle stationary at safe exit, charger disconnected", true, true), default);
Check(physicallyReleased.Accepted && review.Port.Leases.Count == 0,
    "reviewed physical exit and confirmed charging stop allow held resource closure");

var disabled = new Fixture(evidence, "disabled", enabled: false);
disabled.Port.Put("A", 10);
await disabled.CyclesAsync(3);
Check(disabled.Port.Navigations.Count == 0 && disabled.Robot.RelayWrites.Count == 0,
    "disabled automatic charging sends no navigation or relay commands");
Check(!disabled.Service.AllowsNewBusiness("A"), "critical battery blocks new business even when automatic charging is disabled");

var uncommissioned = new Fixture(evidence, "uncommissioned", commissioned: false);
await uncommissioned.CyclesAsync(5);
Check(uncommissioned.Port.Navigations.Count == 0 && uncommissioned.Robot.RelayWrites.Count == 0,
    "an uncommissioned map charging point never enables physical motion");

// The production traffic manager must preserve charging ownership even though a
// charging session is deliberately not a nine-step dispatch business task row.
using (var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(evidence, "traffic.db") }))
{
    var catalog = new TrafficResourceCatalog([new("CHARGE-BAY", TrafficResourceType.ChargingBay)]);
    await new DatabaseInitializer(database, catalog).InitializeAsync(default);
    var traffic = new TrafficResourceManager(database, catalog, new(TimeSpan.FromSeconds(1)));
    Check((await traffic.TryAcquireAsync("CHARGE-A-LOCK", "CHARGE-A", "A", ["CHARGE-BAY"], default)).Acquired,
        "production traffic manager persists a charger reservation without a business task row");
    Check(!(await traffic.TryAcquireAsync("CHARGE-B-LOCK", "CHARGE-B", "B", ["CHARGE-BAY"], default)).Acquired,
        "production capacity-one charging resource rejects the competing vehicle");
    await database.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => l.ExpiresAt == DateTime.Now.AddDays(-1))
        .Where(l => l.TaskId == "CHARGE-A").ExecuteCommandAsync();
    var reconstructed = new TrafficResourceManager(database, catalog, new(TimeSpan.FromSeconds(1)));
    Check((await reconstructed.GetUnavailableResourceIdsAsync("CHARGE-B", default)).Contains("CHARGE-BAY") &&
        !(await reconstructed.TryAcquireAsync("CHARGE-B-LOCK", "CHARGE-B", "B", ["CHARGE-BAY"], default)).Acquired,
        "charger ownership survives heartbeat expiry and production traffic-manager reconstruction");
    await reconstructed.ReleaseTaskAsync("CHARGE-A", default);
    Check((await reconstructed.TryAcquireAsync("CHARGE-B-LOCK", "CHARGE-B", "B", ["CHARGE-BAY"], default)).Acquired,
        "only explicit charging completion release makes the production bay available to the next vehicle");

    var businessSafety = new ChargingBusinessSafety(database);
    await database.Insertable(new DispatchTaskEntity { TaskId = "RESIDUAL-BUSINESS", VehicleId = "A", StationId = "CNC",
        Stage = (int)DispatchTaskStage.Completed, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync();
    await database.Insertable(new TaskEquipmentClaimEntity { EquipmentId = "PLC-DUMPER", TaskId = "RESIDUAL-BUSINESS", AcquiredAtUtc = DateTime.UtcNow }).ExecuteCommandAsync();
    Check(await businessSafety.GetBlockReasonAsync("A", default) is not null,
        "terminal business row with retained PLC equipment ownership still blocks charging");
    await database.Deleteable<TaskEquipmentClaimEntity>().Where(c => c.TaskId == "RESIDUAL-BUSINESS").ExecuteCommandAsync();
    Check(await businessSafety.GetBlockReasonAsync("A", default) is null,
        "closing the original PLC equipment ownership removes its charging blocker");
    await database.Insertable(new AgvCommandOutboxEntity { CommandId = "RESIDUAL-COMMAND", IdempotencyKey = "RESIDUAL-COMMAND",
        VehicleId = "A", TaskId = "RESIDUAL-BUSINESS", StepId = "STEP", PayloadJson = "{}", Status = (int)AgvCommandStatus.Pending,
        NextAttemptAt = DateTime.Now, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync();
    foreach (var state in new[] { AgvCommandStatus.Pending, AgvCommandStatus.Sending, AgvCommandStatus.RetryScheduled, AgvCommandStatus.Failed })
    {
        await database.Updateable<AgvCommandOutboxEntity>().SetColumns(c => c.Status == (int)state)
            .Where(c => c.CommandId == "RESIDUAL-COMMAND").ExecuteCommandAsync();
        Check(await businessSafety.GetBlockReasonAsync("A", default) is not null,
            "unclosed business command blocks charging despite terminal task row: " + state);
    }
    await database.Updateable<AgvCommandOutboxEntity>().SetColumns(c => c.Status == (int)AgvCommandStatus.Cancelled)
        .Where(c => c.CommandId == "RESIDUAL-COMMAND").ExecuteCommandAsync();
    Check(await businessSafety.GetBlockReasonAsync("A", default) is null,
        "a conclusively cancelled original command does not permanently block charging");
}

var businessPort = new BusinessPort();
var businessOwner = new BusinessRunOwnership();
var businessGuard = new BusinessGuard();
var business = new BusinessRunService(businessPort, businessOwner, Path.Combine(evidence, "business-handover.json"), businessGuard);
await business.StartAsync(admin, new(Guid.NewGuid().ToString("N"), "Cycle", "A", ["CNC"], 3, "HOME", true), default);
await business.TickAsync(default);
Check((await business.ReadAsync(default)).Current?.Stage == "Executing" && businessPort.Creates == 1,
    "business handover fixture starts the current box cycle exactly once");
businessGuard.Allow = false;
await business.TickAsync(default);
Check((await business.ReadAsync(default)).Current?.Stage == "Executing" && businessOwner.Active && businessPort.Returns == 0,
    "a low battery charging request preserves the executing box cycle");
businessPort.TaskState = "Completed"; businessPort.Active = false; businessPort.Closed = false;
await business.TickAsync(default);
Check((await business.ReadAsync(default)).Current?.Stage == "Executing" && businessOwner.Active,
    "business completion cannot hand over charging while device responsibility remains open");
businessPort.Closed = true;
await business.TickAsync(default);
businessPort.Closed = false;
await business.TickAsync(default);
Check((await business.ReadAsync(default)).Current?.Stage == "Waiting" && businessOwner.Active,
    "waiting business still retains ownership until all handshake responsibility is closed");
businessPort.Closed = true; businessPort.Stopped = false;
await business.TickAsync(default);
Check((await business.ReadAsync(default)).Current?.Stage == "Waiting" && businessOwner.Active,
    "completed business must also stop the vehicle before charging handover");
businessPort.Stopped = true;
await business.TickAsync(default);
var handedToCharge = (await business.ReadAsync(default)).Current;
Check(handedToCharge is { Stage: "WaitingForCharge", ChargeResumePending: true, ChargeSimulatorPaused: true } &&
    !businessOwner.Active && businessPort.Calls.SequenceEqual(["CNC"]) &&
    businessPort.Creates == 1 && businessPort.Returns == 0 && businessPort.Releases == 0 && businessPort.SimulatorStops == 1,
    "safe charging handover retains the original plan, leaves calls latched, stops the simulator, and sends no standby or new task");

Console.WriteLine($"PASS: {checks} charging workflow and fault checks. Synthetic hardware evidence only; no real charging acceptance is claimed.");

sealed class Fixture
{
    public FakeClock Clock { get; } = new();
    public FakePort Port { get; }
    public FakeRobot Robot { get; }
    public ChargingOptions Options { get; }
    public ChargingService Service { get; private set; }
    public bool RefreshBatteryTimestamp { get; set; } = true;
    private readonly string path;
    public ChargingStatus Status => Service.ReadAsync(default).GetAwaiter().GetResult();
    public ChargingSession Current => Status.Sessions.Last(s => s.VehicleId == "A");
    public Fixture(string root, string name, bool enabled = true, bool commissioned = true)
    {
        path = Path.Combine(root, name + ".json");
        Port = new(Clock); Robot = new(Clock);
        Port.IntentExists = command => File.Exists(path) && File.ReadAllText(path).Contains(command, StringComparison.Ordinal);
        Options = new() { Enabled = enabled, Sites = [new("BAY-1", "CHARGER", "EXIT", "CHARGE-RESOURCE")
            { Commissioned = commissioned, AllowedVehicleIds = ["A", "B"] }] };
        Port.Put("A", 20); Robot.Battery = new("A", 20, false, 0, 25, false, false, Clock.GetUtcNow());
        Service = NewService();
    }
    private ChargingService NewService() => new(Options, Port, Robot, path, NullLogger<ChargingService>.Instance, Clock);
    public void Restart() => Service = NewService();
    public void Battery(decimal percent, bool charging, double current, double temperature = 25)
    {
        Robot.Battery = new("A", percent, charging, current, temperature, false, false, Clock.GetUtcNow());
        Port.Put("A", percent, charging, current, temperature);
    }
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
            if (RefreshBatteryTimestamp) Robot.Battery = Robot.Battery with { ObservedAt = Clock.GetUtcNow() };
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
    public Exception? StopFailure { get; set; }
    public Exception? StartFailure { get; set; }
    public Task SetChargingRelayAsync(string vehicleId, bool enabled, CancellationToken ct)
    {
        RelayWrites.Add(enabled);
        if (!enabled && StopFailure is not null) throw StopFailure;
        if (enabled && StartFailure is not null) throw StartFailure;
        return Task.CompletedTask;
    }
    public Task<ChargingBatteryReading> ReadBatteryAsync(string vehicleId, CancellationToken ct)
    { if (ReadFailure is not null) throw ReadFailure; return Task.FromResult(Battery with { VehicleId = vehicleId }); }
}

sealed class FakePort(FakeClock clock) : IChargingRuntimePort
{
    private readonly Dictionary<string, AgvRobotSnapshot> vehicles = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Busy { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Leases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Command, string Vehicle, string[] Path)> Navigations { get; } = [];
    public HashSet<string> Completed { get; } = [];
    public Exception? NavigationFailure { get; set; }
    public bool Stopped { get; set; } = true;
    public bool ExecutionBlocked { get; set; }
    public string? ValidationError { get; set; }
    public int RecoveryControlAcquisitions { get; private set; }
    public Func<string, bool> IntentExists { get; set; } = _ => false;
    public bool IntentPersistedBeforeEveryNavigation { get; private set; } = true;
    public int ReleaseCount { get; private set; }
    public IReadOnlyList<AgvRobotSnapshot> Vehicles => vehicles.Values.ToArray();
    public string? NewWorkBlockReason => ExecutionBlocked ? "Execution gate is closed" : null;
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
    public ValueTask<IDisposable?> EnterExecutionAsync(bool newWork, CancellationToken ct) =>
        ValueTask.FromResult<IDisposable?>(ExecutionBlocked ? null : new Lease());
    public ValueTask<IDisposable?> EnterRecoveryControlAsync(CancellationToken ct)
    { RecoveryControlAcquisitions++; return ValueTask.FromResult<IDisposable?>(new Lease()); }
    public Task<string?> BusyReasonAsync(string vehicleId, string? ownSessionId, CancellationToken ct) =>
        Task.FromResult(Busy.Contains(vehicleId) ? "Original business/equipment is still active" : null);
    public Task<ChargingRoute?> PlanAsync(string vehicleId, ChargingSite site, CancellationToken ct) => Task.FromResult<ChargingRoute?>(
        new("SYNTHETIC-MAP", "V1", [Sites[vehicleId], site.ChargingSiteId], [site.ChargingSiteId, site.ExitSiteId], [site.ResourceId], 5));
    public ChargingWaitingRoute? WaitingRoute { get; set; }
    public Dictionary<string, ChargingApproachEstimate> ApproachEstimates { get; } = [];
    public Task<ChargingApproachEstimate?> EstimateApproachAsync(string vehicleId, ChargingSite site, string sessionId,
        CancellationToken ct) => Task.FromResult(ApproachEstimates.GetValueOrDefault(site.Id));
    public Task<ChargingWaitingRoute?> PlanWaitingAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(WaitingRoute);
    public Task<bool> ReserveWaitingAsync(ChargingSession session, CancellationToken ct)
    {
        foreach (var id in session.WaitingRoute!.ResourceIds) Leases[id] = session.SessionId;
        return Task.FromResult(true);
    }
    public Task TrimWaitingAsync(ChargingSession session, bool departed, CancellationToken ct)
    {
        foreach (var id in session.WaitingRoute!.ResourceIds.Where(id => departed || id != session.WaitingRoute.ParkingResourceId))
            Leases.Remove(id);
        return Task.CompletedTask;
    }
    public Task<string?> ValidateAsync(ChargingSession session, bool requireStopped, CancellationToken ct) =>
        Task.FromResult(ValidationError ?? (requireStopped && !Stopped ? "Vehicle is still moving; no physical stationary proof" : null));
    public bool IsAt(string vehicleId, string siteId) => Sites.GetValueOrDefault(vehicleId) == siteId;
    public Task<bool> ReserveAsync(ChargingSession session, CancellationToken ct)
    {
        if (Leases.TryGetValue(session.Site!.ResourceId, out var owner) && owner != session.SessionId) return Task.FromResult(false);
        Leases[session.Site.ResourceId] = session.SessionId;
        return Task.FromResult(true);
    }
    public Task RenewAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseAsync(string sessionId, CancellationToken ct)
    {
        foreach (var resource in Leases.Where(p => p.Value == sessionId).Select(p => p.Key).ToArray()) Leases.Remove(resource);
        ReleaseCount++;
        return Task.CompletedTask;
    }
    public Task<string> NavigateAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct)
    {
        IntentPersistedBeforeEveryNavigation &= IntentExists(commandId);
        Navigations.Add((commandId, vehicleId, path.ToArray()));
        if (NavigationFailure is not null) throw NavigationFailure;
        return Task.FromResult(commandId);
    }
    public Task<AgvTransportTaskStatus?> ObserveNavigationAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct) =>
        Task.FromResult<AgvTransportTaskStatus?>(new(commandId, commandId,
            Completed.Contains(commandId) ? AgvTransportTaskState.Completed : AgvTransportTaskState.GoingToDestination, vehicleId, clock.GetUtcNow()));
    private sealed class Lease : IDisposable { public void Dispose() { } }
}

sealed class BusinessGuard : IChargingDispatchGuard
{
    public bool Allow { get; set; } = true;
    public bool HasActiveSessions => false;
    public bool OwnsVehicle(string vehicleId) => false;
    public bool AllowsNewBusiness(string vehicleId) => Allow;
    public ValueTask<IDisposable> EnterBusinessAsync(CancellationToken ct) => ValueTask.FromResult<IDisposable>(new Lease());
    private sealed class Lease : IDisposable { public void Dispose() { } }
}

sealed class BusinessPort : IBusinessRunPort
{
    public string[] Calls { get; set; } = ["CNC"];
    public bool Closed { get; set; } = true;
    public bool Active { get; set; }
    public bool Stopped { get; set; } = true;
    public string TaskState { get; set; } = "PickingUp";
    public int Creates { get; private set; }
    public int Returns { get; private set; }
    public int Releases { get; private set; }
    public int SimulatorStops { get; private set; }
    public Task ValidateAsync(BusinessRunStart plan, CancellationToken ct) => Task.CompletedTask;
    public Task<BusinessRunObservation> ObserveAsync(BusinessRunRecord run, CancellationToken ct) => Task.FromResult(
        new BusinessRunObservation("SYNTHETIC-MAP", "V1", "PARAMETERS", null, Calls.ToArray(), Closed, Active, "WORK", Stopped, TaskState, false));
    public Task<string> CreateAsync(BusinessRunRecord run, string station, string requestId, CancellationToken ct)
    { Creates++; Active = true; return Task.FromResult("BOX-CYCLE-" + Creates); }
    public Task<string> ReturnAsync(BusinessRunRecord run, CancellationToken ct)
    { Returns++; return Task.FromResult("STANDBY-UNEXPECTED"); }
    public Task SimulatorAsync(BusinessRunRecord run, bool stop, CancellationToken ct)
    { if (stop) SimulatorStops++; return Task.CompletedTask; }
    public Task ReleaseReturnAsync(BusinessRunRecord run, CancellationToken ct)
    { Releases++; return Task.CompletedTask; }
}
