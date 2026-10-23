using System.Net;
using System.Net.Sockets;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
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
var service = new CommunicationSettingsService(path, root, settings, fleet, tasks, NullLogger<CommunicationSettingsService>.Instance);
var manager = new TrafficUserContext("TEST\\engineer", "工程师", TrafficPermission.View | TrafficPermission.ManageConnections);
var viewer = new TrafficUserContext("TEST\\viewer", "查看", TrafficPermission.View);
var before = await service.GetAsync(manager, default);
var draft = before.Saved; draft.Agvs[0].DisplayName = "Test AGV";
try { await service.SaveAsync(viewer, new(before.Revision, draft), default); Require(false, "viewer denied"); } catch (UnauthorizedAccessException) { }
var saved = await service.SaveAsync(manager, new(before.Revision, draft), default);
Require(saved.RestartRequired && saved.Active.Agvs[0].DisplayName != "Test AGV" && saved.Audits.Single().Operator == manager.WindowsIdentity, "save does not mutate runtime and audited");
Require(CommunicationSettingsService.ReadDocument(path)!.Configuration.Plcs.Count == 2, "persistent profiles");
try { await service.SaveAsync(manager, new(before.Revision, draft), default); Require(false, "stale revision denied"); } catch (InvalidOperationException) { }
tasks.HasTask = true;
try { await service.SaveAsync(manager, new(saved.Revision, draft), default); Require(false, "active task denied"); } catch (InvalidOperationException) { }
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var accepted = listener.AcceptTcpClientAsync();
var probe = await service.ProbeAsync(manager, new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), default);
using var socket = await accepted;
Require(probe.Reachable && socket.Available == 0, "TCP probe sends no protocol data");
Console.WriteLine("PASS: multi PLC catalog/routing, scoped signal quality, validation, permissions, atomic save, revision, active-task guard and TCP-only probe");
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
