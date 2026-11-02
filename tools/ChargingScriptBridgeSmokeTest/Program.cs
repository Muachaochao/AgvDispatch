using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Service;

// Filesystem/identity tests only. The dummy worker must never be launched.
var root = Path.Combine(Path.GetTempPath(), "charging-bridge-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
async Task Rejects<T>(Func<Task> action, string message) where T : Exception
{ try { await action(); } catch (T) { Check(true, message); return; } throw new Exception(message); }
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, json));
try
{
    var profilePath = Path.Combine(root, "profile.json");
    Write(profilePath, new { schema = "agv-dispatch-charge-profile-v1", commissioned = true, vehicleId = "A", siteId = "C",
        robotIp = "192.0.2.66", robotMap = "robot-map", robotMapMd5 = new string('a', 32), approachSite = "LM52" });
    var tools = Path.Combine(root, "tools"); Directory.CreateDirectory(tools);
    File.WriteAllText(Path.Combine(tools, "dispatch_worker.py"), "raise AssertionError('must never execute in offline bridge tests')");
    var settings = new ChargingScriptOptions { Enabled = true, PythonExecutable = "MUST-NOT-LAUNCH", ToolsDirectory = tools,
        Profiles = [new() { SiteId = "C", VehicleId = "A", ProfilePath = profilePath }] };
    var agvs = new AgvOptions { Enabled = true, AllowCommands = true,
        Robots = [new() { VehicleId = "A", IpAddress = "192.0.2.66" }],
        MapBindings = [new() { VehicleId = "A", LocalMapId = "m", LocalMapVersion = "v", RobotMap = "robot-map", RobotMapMd5 = new string('a', 32), CoordinatesVerified = true }] };
    var adapter = new ScriptChargingWorkflow(settings, new(), agvs, root);
    var site = new ChargingSite("C", "LM52", "LM51", "BAY") { Commissioned = true, ControlMode = "RobokitScript", AllowedVehicleIds = ["A"] };
    var id = "CHARGE-test-DOCK";
    var session = new ChargingSession("CHARGE-test", "A", ChargingPhase.SendingDock, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test")
    { Site = site, Route = new("m", "v", ["LM44", "LM52"], ["LM52", "LM51"], ["BAY"], 1),
        ScriptOperationId = id, ScriptOperationKind = ChargingWorkflowKind.Dock };
    Check(adapter.IsConfigured(site, "A"), "commissioned per-vehicle profile is selected");
    Check(!adapter.IsConfigured(site, "B"), "another vehicle cannot reuse that calibration");
    settings.Enabled = false;
    Check(!adapter.IsConfigured(site, "A"), "script feature defaults fail closed");
    settings.Enabled = true;
    var missing = await adapter.ObserveAsync(session, default);
    Check(missing.State == ChargingWorkflowState.Unknown && !Directory.Exists(Path.Combine(root, settings.EvidenceDirectory)), "observation does not create or launch missing operation");
    await Rejects<InvalidOperationException>(() => adapter.StartAsync(session, "different", ChargingWorkflowKind.Dock, default), "start must match durable operation identity");
    var directory = Path.Combine(root, settings.EvidenceDirectory, id); Directory.CreateDirectory(directory);
    var manifestPath = Path.Combine(directory, "manifest.json");
    Write(manifestPath, new { operationId = id, sessionId = session.SessionId, vehicleId = "A", kind = "Dock" });
    await Rejects<IOException>(() => adapter.StartAsync(session, id, ChargingWorkflowKind.Dock, default), "existing launch intent never starts a second process");
    var good = new ChargingWorkflowStatus(id, ChargingWorkflowKind.Dock, ChargingWorkflowState.Running, DateTimeOffset.UtcNow, "observing");
    Write(Path.Combine(directory, "worker_state.json"), good);
    var current = await adapter.ObserveAsync(session, default);
    Check(current.State == ChargingWorkflowState.Running && File.Exists(Path.Combine(directory, "lease.json")), "fresh observation renews the original worker lease");
    Write(Path.Combine(directory, "worker_state.json"), good with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
    Check((await adapter.ObserveAsync(session, default)).State == ChargingWorkflowState.Unknown, "stale worker observations are never treated as progress");
    Write(Path.Combine(directory, "result.json"), good with { State = ChargingWorkflowState.Succeeded, OperationId = "other" });
    Check((await adapter.ObserveAsync(session, default)).State == ChargingWorkflowState.Unknown, "wrong final operation identity is rejected");
    Write(Path.Combine(directory, "result.json"), good with { State = ChargingWorkflowState.Succeeded });
    Check((await adapter.ObserveAsync(session, default)).State == ChargingWorkflowState.Succeeded, "matching durable final result is observed without replay");
    await adapter.RequestStopAsync(session, default);
    var stopBefore = File.ReadAllText(Path.Combine(directory, "stop.json"));
    await adapter.RequestStopAsync(session, default);
    Check(File.ReadAllText(Path.Combine(directory, "stop.json")) == stopBefore, "stop persists once and never kills process as proof of stopping");
    var undock = session with { ScriptOperationId = "CHARGE-test-UNDOCK", ScriptOperationKind = ChargingWorkflowKind.Undock };
    await Rejects<InvalidOperationException>(() => adapter.StartAsync(undock, undock.ScriptOperationId!, ChargingWorkflowKind.Undock, default), "undock requires original verified charge shutdown");
    agvs.MapBindings[0].CoordinatesVerified = false;
    await Rejects<InvalidOperationException>(() => adapter.StartAsync(session, id, ChargingWorkflowKind.Dock, default), "backend map coordinate binding is required before worker launch");
    Console.WriteLine($"Charging script bridge smoke passed: {count}");
}
finally { Directory.Delete(root, true); }
