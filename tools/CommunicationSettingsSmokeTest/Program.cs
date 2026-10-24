using System.Net;
using System.Net.Sockets;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.Combine(Path.GetTempPath(), "agv-communications-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "config"));
var header = "address,variableName,chineseName,stationId,deviceType,instanceIndex,dataType,direction,access,addressStatus,enabled,setConditionZh,resetConditionZh,sourceNote\n";
await File.WriteAllTextAsync(Path.Combine(root, "config", "a.csv"), header + "DB1.DBX0.0,BoxPresent,箱体,CNC001,ChipRemovalStation,1,BOOL,PLC->Host,Read,Confirmed,true,,,test\n");
await File.WriteAllTextAsync(Path.Combine(root, "config", "b.csv"), header + "DB1.DBX0.0,BoxPresent,箱体,CNC002,ChipRemovalStation,2,BOOL,PLC->Host,Read,Confirmed,true,,,test\n");
var settings = new CommunicationConfiguration
{
    Plcs = [new() { DeviceId = "PLC-01", SignalCsvPath = "config/a.csv" }, new() { DeviceId = "PLC-02", SignalCsvPath = "config/b.csv" }],
    Agvs = [new() { VehicleId = "AGV-01" }, new() { VehicleId = "AGV-02" }]
};
CommunicationSettingsService.Validate(settings, root);
var bindingOnly=System.Text.Json.JsonSerializer.Deserialize<CommunicationConfiguration>(System.Text.Json.JsonSerializer.Serialize(settings))!;
bindingOnly.MapBindings=[new(){VehicleId="AGV-01",LocalMapId="MAP",LocalMapVersion="V1",RobotMap="test",RobotMapMd5=new string('a',32)}];
Require(CommunicationSettingsService.OnlyMapBindingsDiffer(settings,bindingOnly),"map binding-only persistence distinguished from device changes");
bindingOnly.Agvs[0].IpAddress="192.0.2.20";
Require(!CommunicationSettingsService.OnlyMapBindingsDiffer(settings,bindingOnly),"endpoint changes cannot use map binding-only path");
using var fleet = new PlcFleet(settings.Plcs.Select(p => CommunicationSettingsService.ToPlcOptions(p, new PlcOptions(), root)).ToArray());
Require(fleet.Connections.Count == 2 && fleet.Catalog.Count == 2, "multiple PLC connections and identical DB address isolated");
var cache = new PlcSignalCache(fleet.Catalog);
cache.ApplyReadings([new("CNC001", "BoxPresent", true, DateTimeOffset.Now), new("CNC002", "BoxPresent", true, DateTimeOffset.Now)]);
cache.MarkEnabledSignals(SignalQuality.Bad, "PLC-01 failed", new HashSet<string> { "CNC001" });
Require(cache.GetStation("CNC001").Single().Quality == SignalQuality.Bad && cache.GetStation("CNC002").Single().Quality == SignalQuality.Good, "failure quality scoped to owning PLC");
Require(await fleet.ReadSignalAsync("CNC001", "BoxPresent", default) is null, "disabled PLC stays disconnected");
try { await fleet.WriteSignalAsync("CNC002", "BoxPresent", true, default); Require(false, "write rejected"); } catch (InvalidOperationException) { }
settings.Plcs[1].SignalCsvPath = "config/a.csv";
Reject(() => CommunicationSettingsService.Validate(settings, root), "duplicate station ownership");
settings.Plcs[1].SignalCsvPath = "../outside.csv";
Reject(() => CommunicationSettingsService.Validate(settings, root), "path traversal");
settings.Plcs[1].SignalCsvPath = "config/b.csv";
settings.Agvs[1].VehicleId = "AGV-01";
Reject(() => CommunicationSettingsService.Validate(settings, root), "duplicate vehicle id");
settings.Agvs[1].VehicleId = "AGV-02";
settings.AgvEnabled = true;
settings.Agvs[0].Enabled = true;
Reject(() => CommunicationSettingsService.Validate(settings, root), "enabled AGV without endpoint");
settings.AgvEnabled = false;
settings.Agvs[0].Enabled = false;
var tasks = new TestTasks();
var path = Path.Combine(root, "data", "communication-settings.json");
var recovery = new TestRecoveryBarrier();
var gate = new MapExecutionGate(recovery);
using (var control = await gate.EnterControlAsync(default)) gate.SetMode(MapExecutionMode.Running);
var service = new CommunicationSettingsService(path, root, settings, fleet, tasks, NullLogger<CommunicationSettingsService>.Instance, gate);
var manager = new TrafficUserContext("TEST\\engineer", "工程师", TrafficPermission.View | TrafficPermission.ManageConnections);
var viewer = new TrafficUserContext("TEST\\viewer", "查看", TrafficPermission.View);
var before = await service.GetAsync(manager, default);
var activeBefore = System.Text.Json.JsonSerializer.Serialize(before.Active);
var draft = before.Saved; draft.Agvs[0].DisplayName = "Test AGV";
try { await service.SaveAsync(viewer, new(before.Revision, draft), default); Require(false, "viewer denied"); } catch (UnauthorizedAccessException) { }
Require(!File.Exists(path), "unauthorized save creates no configuration file");
Require(!recovery.IsOpen && !gate.CanStartNewWork && await gate.EnterAsync(MapExecutionOperation.NewWork, default) is null,
    "startup recovery blocks execution before configuration save");
var saved = await service.SaveAsync(manager, new(before.Revision, draft), default);
Require(saved.RestartRequired && saved.Active.Agvs[0].DisplayName != "Test AGV" && saved.Audits.Single().Operator == manager.WindowsIdentity, "save does not mutate runtime and audited");
Require(System.Text.Json.JsonSerializer.Serialize(saved.Active) == activeBefore && fleet.Connections.Count == 2,
    "persistent connection edits leave full active configuration and connection fleet unchanged");
Require(!recovery.IsOpen && !gate.CanStartNewWork && await gate.EnterAsync(MapExecutionOperation.NewWork, default) is null,
    "save while recovery is closed preserves execution barrier");
var bindingDraft = saved.Saved;
bindingDraft.MapBindings = [new() { VehicleId = "AGV-01", LocalMapId = "MAP", LocalMapVersion = "V1", RobotMap = "test", RobotMapMd5 = new string('a', 32) }];
saved = await service.SaveAsync(manager, new(saved.Revision, bindingDraft), default);
draft = saved.Saved;
Require(saved.Saved.MapBindings!.Count == 1 && System.Text.Json.JsonSerializer.Serialize(saved.Active) == activeBefore &&
    !recovery.IsOpen && !gate.CanStartNewWork, "map binding save shares recovery-independent persistence without runtime or barrier changes");
Require(CommunicationSettingsService.ReadDocument(path)!.Configuration.Plcs.Count == 2, "persistent profiles");
Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "successful atomic save leaves no temporary file");
var savedBytes = await File.ReadAllBytesAsync(path);
try { await service.SaveAsync(manager, new(before.Revision, draft), default); Require(false, "stale revision denied"); } catch (InvalidOperationException) { }
Require((await File.ReadAllBytesAsync(path)).SequenceEqual(savedBytes), "stale revision does not overwrite configuration");

foreach (var mode in new[] { MapExecutionMode.Starting, MapExecutionMode.Draining, MapExecutionMode.Held })
{
    using (var control = await gate.EnterControlAsync(default)) gate.SetMode(mode);
    var rejected = false;
    try { await service.SaveAsync(manager, new(saved.Revision, draft), default); }
    catch (InvalidOperationException error) { rejected = !string.IsNullOrWhiteSpace(error.Message); }
    Require(rejected, mode + " rejects connection settings save");
    Require((await service.GetAsync(manager, default)).Revision == saved.Revision &&
        (await File.ReadAllBytesAsync(path)).SequenceEqual(savedBytes), mode + " rejection preserves revision and file bytes");
}
using (var control = await gate.EnterControlAsync(default)) gate.SetMode(MapExecutionMode.Running);

var missingCsv = (await service.GetAsync(manager, default)).Saved;
missingCsv.Plcs[0].Enabled = true;
missingCsv.Plcs[0].IpAddress = "127.0.0.1";
missingCsv.Plcs[0].SignalCsvPath = "";
var csvRejected = false;
try { await service.SaveAsync(manager, new(saved.Revision, missingCsv), default); }
catch (ArgumentException error) { csvRejected = error.Message.Contains("PLC-01") && error.Message.Contains("CSV"); }
Require(csvRejected, "enabled PLC without CSV reports actionable PLC and point-table validation error");
Require((await service.GetAsync(manager, default)).Revision == saved.Revision &&
    (await File.ReadAllBytesAsync(path)).SequenceEqual(savedBytes), "invalid PLC configuration does not change saved revision or file");
tasks.HasTask = true;
try { await service.SaveAsync(manager, new(saved.Revision, draft), default); Require(false, "active task denied"); } catch (InvalidOperationException) { }
Require((await File.ReadAllBytesAsync(path)).SequenceEqual(savedBytes), "active task rejection preserves configuration file");
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var accepted = listener.AcceptTcpClientAsync();
var probe = await service.ProbeAsync(manager, new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), default);
using var socket = await accepted;
Require(probe.Reachable && socket.Available == 0, "TCP probe sends no protocol data");
Console.WriteLine("PASS: multi PLC catalog/routing, validation, permissions, atomic save, recovery isolation, real map-switch guards, revision, active-task guard and TCP-only probe");
Console.WriteLine(root);

static void Reject(Action operation, string name)
{
    try { operation(); Require(false, name); } catch (ArgumentException) { }
}
static void Require(bool condition, string name)
{
    if (!condition) { Console.Error.WriteLine("FAIL: " + name); Environment.Exit(1); }
}
sealed class TestTasks : IActiveTaskLookup
{
    public bool HasTask { get; set; }
    public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken token) =>
        Task.FromResult<IReadOnlyList<ActiveDispatchTask>>(HasTask
            ? [new("TASK", "CNC001", null, null, DispatchTaskStage.Faulted, DateTimeOffset.Now, DateTimeOffset.Now)] : []);
}
sealed class TestRecoveryBarrier : IRecoveryBarrier
{
    public bool IsOpen => false;
}
