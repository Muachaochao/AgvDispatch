using System.Reflection;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Service;

var root = Path.Combine(Path.GetTempPath(), "plc-cycle-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var tests = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); tests++; Console.WriteLine("PASS " + message); }
var options = new PlcCycleTestOptions { Enabled = true, VehicleId = "AGV-01", StageTimeoutSeconds = 10 };
var user = TrafficUserContext.LocalOperation("isolated-tests");
var field = typeof(PlcCycleTestService).GetField("_record", BindingFlags.Instance | BindingFlags.NonPublic)!;
async Task Tick(PlcCycleTestService service) => await (Task)typeof(PlcCycleTestService)
    .GetMethod("ProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [CancellationToken.None])!;
PlcCycleTestRecord Record(PlcCycleTestService service) => (PlcCycleTestRecord)field.GetValue(service)!;
void Set(PlcCycleTestService service, PlcCycleTestRecord record) => field.SetValue(service, record);
async Task<(PlcCycleTestService Service, FakePort Port, FakeNavigation Navigation, string Path)> Create()
{
    var port = new FakePort(); var navigation = new FakeNavigation(port);
    var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
    var service = new PlcCycleTestService(options, port, navigation, path);
    await service.ArmAsync(user, new(Guid.NewGuid().ToString("N"), "LM2", "LM5", "LM6", true, true, true), default);
    return (service, port, navigation, path);
}

// Full fake cycle exercises every stage; no sockets or actual PLC/vehicle are used.
var full = await Create();
for (var i = 0; i < 50 && Record(full.Service).Stage != "Completed"; i++)
{
    var stage = Record(full.Service).Stage;
    full.Port.Set("CNC-001", "FullBoxRequested", stage == "ArmedAwaitCall");
    full.Port.Set("DUMPER-001", "DumperReady", stage == "AwaitDumperReady");
    full.Port.Set("DUMPER-001", "DumpingCompletedMayLeave", stage is "AwaitDone" or "NavigateReturn" or "AwaitNavigationToReturn" or "ClearDumping");
    await Tick(full.Service);
    if (Record(full.Service).Stage == "Hold") throw new Exception(Record(full.Service).Message);
}
Check(Record(full.Service).Stage == "Completed" && full.Navigation.Submits == 5, "complete cycle has exactly three navigations and two jack actions");
Check(full.Port.Values.Where(p => p.Key.Item2 is not ("BoxPresent" or "CommunicationOnline")).All(p => !p.Value), "all handshake outputs and inputs close to zero");
var archivedRequest = Record(full.Service).RequestId;
await full.Service.ArmAsync(user, new(Guid.NewGuid().ToString("N"), "LM2", "LM5", "LM6", true, true, true), default);
Check(Directory.GetFiles(full.Path + ".history").Length == 1, "previous cycle archived before rearming");
var same = Record(full.Service);
await full.Service.ArmAsync(user, new(same.RequestId, "LM2", "LM5", "LM6", true, true, true), default);
Check(Record(full.Service).CycleId == same.CycleId && full.Navigation.Submits == 5, "idempotent arm never creates a second cycle");
Set(full.Service, Record(full.Service) with { Stage = "Completed" });
try { await full.Service.ArmAsync(user, new(archivedRequest,"LM2","LM5","LM6",true,true,true),default);throw new Exception("historical request armed another cycle"); }
catch (InvalidOperationException e) when(e.Message.Contains("历史联调")) { Check(Record(full.Service).CycleId == same.CycleId,"historical request replay cannot create a new cycle"); }

foreach (var stage in new[] { "ArmedAwaitCall", "AwaitJackUp", "AwaitJackDown", "AwaitNavigationToWaiting", "AwaitDumperReady", "AwaitDoneCleared", "NavigateReturn" })
{
    var f = await Create();
    File.WriteAllText(f.Path, JsonSerializer.Serialize(Record(f.Service) with { Stage = stage }));
    var restarted = new PlcCycleTestService(options, f.Port, f.Navigation, f.Path);
    await Tick(restarted);
    Check(Record(restarted).Stage == "Hold" && f.Port.Writes == 0 && f.Navigation.Submits == 0, "restart freezes " + stage);
}

var timeout = await Create();
Set(timeout.Service, Record(timeout.Service) with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
await Tick(timeout.Service);
Check(Record(timeout.Service).Stage == "Hold" && timeout.Port.Writes == 0, "timeout freezes without writes");

foreach (var (stage, signal) in new[] { ("NotifyWaiting", "DumperReady"), ("NotifyDumping", "DumpingCompletedMayLeave") })
{
    var f = await Create(); Set(f.Service, Record(f.Service) with { Stage = stage });
    f.Port.Set("DUMPER-001", signal, true); await Tick(f.Service);
    Check(Record(f.Service).Stage == "Hold" && f.Port.Writes == 0, "old high cannot grant " + signal);
}

var between = await Create();
Set(between.Service, Record(between.Service) with { Stage = "NavigateWaiting" });
await Tick(between.Service);
between.Port.Station = null;
await Tick(between.Service);
Check(between.Navigation.Reads > 1 && Record(between.Service).Stage == "NotifyWaiting", "between-station reports still query original navigation");

var replaced = await Create();
Set(replaced.Service, Record(replaced.Service) with { Stage = "NavigateWaiting" });
await Tick(replaced.Service);
replaced.Navigation.Current = replaced.Navigation.Current! with { TaskId = "other-task", VendorTaskId = "other-vendor-task" };
await Tick(replaced.Service);
Check(Record(replaced.Service).Stage == "Hold", "same target with another task identity cannot advance");

var uncertain = await Create();
Set(uncertain.Service, Record(uncertain.Service) with { Stage = "JackUpStart" });
await Tick(uncertain.Service);
uncertain.Navigation.Jack = uncertain.Navigation.Jack! with { State = "Uncertain", Acknowledged = false };
await Tick(uncertain.Service);
Check(Record(uncertain.Service).Stage == "Hold", "target telemetry cannot certify uncertain jack command");

var lost = await Create(); lost.Port.FailWrite = true; lost.Port.Set("CNC-001", "FullBoxRequested", true);
await Tick(lost.Service);
Check(Record(lost.Service).Stage == "Hold" && Record(lost.Service).PendingWrite is not null, "lost PLC response preserves intent and freezes");
lost.Port.Set("CNC-001", "FullBoxRequested", false);
try { await lost.Service.ResetAsync(user, new(Record(lost.Service).CycleId, "现场核对测试未决写入禁止自动覆盖"), default); throw new Exception("reset accepted uncertain write"); }
catch (InvalidOperationException) { Check(lost.Port.Writes == 1, "reset does not retry unresolved write"); }

foreach (var (station, signal) in new[] { ("CNC-001", "FullBoxRequested"), ("DUMPER-001", "DumperReady"), ("DUMPER-001", "DumpingCompletedMayLeave") })
{
    var f = await Create(); Set(f.Service, Record(f.Service) with { Stage = "Hold" }); f.Port.Set(station, signal, true);
    try { await f.Service.ResetAsync(user, new(Record(f.Service).CycleId, "现场核对输入信号没有撤销不能复位"), default); throw new Exception("reset accepted high input"); }
    catch (InvalidOperationException) { Check(f.Port.Writes == 0, "reset requires low " + signal); }
}
Check(!PlcCycleTestPort.AllPlcsAreLoopback([]), "empty PLC configuration is not a simulator");
Check(!PlcCycleTestPort.AllPlcsAreLoopback([new() { Enabled = true, IpAddress = "127.0.0.1", Port = 102 }]), "production port not allowed");
Check(PlcCycleTestPort.AllPlcsAreLoopback([new() { Enabled = true, IpAddress = "127.0.0.1", Port = 10200 }]), "dedicated simulator endpoint allowed");
var unhealthy = await Create(); unhealthy.Port.Healthy = false;
await Tick(unhealthy.Service); unhealthy.Port.Healthy = true; unhealthy.Port.Set("CNC-001", "FullBoxRequested", true);
await Tick(unhealthy.Service);
Check(Record(unhealthy.Service).Stage == "Hold" && unhealthy.Port.Writes == 0, "connection/health recovery never automatically restores cycle permission");
var resetFailure = await Create();
Set(resetFailure.Service, Record(resetFailure.Service) with { Stage = "Completed" });
resetFailure.Port.Set("CNC-001", "FullBoxRequestAcknowledged", true); resetFailure.Port.FailWrite = true;
try { await resetFailure.Service.ResetAsync(user, new(Record(resetFailure.Service).CycleId, "现场核对复位回包丢失保留原记录"), default); throw new Exception("write unexpectedly succeeded"); }
catch (IOException) { }
try { await resetFailure.Service.ArmAsync(user, new(Guid.NewGuid().ToString("N"), "LM2", "LM5", "LM6", true, true, true), default); throw new Exception("pending reset write overwritten"); }
catch (InvalidOperationException) { Check(Record(resetFailure.Service).PendingWrite is not null && resetFailure.Port.Writes == 1, "failed reset from completed record also blocks rearming"); }
Console.WriteLine($"{tests} assertions passed. Evidence: {root}");

sealed class FakePort : IPlcCycleTestPort
{
    public readonly Dictionary<(string, string), bool> Values = new();
    public string? Station = "LM2";
    public int Writes;
    public bool FailWrite;
    public bool Healthy = true;
    public const string Md5 = "0123456789abcdef0123456789abcdef";
    public FakePort()
    {
        foreach (var station in new[] { "CNC-001", "CNC-002" })
            foreach (var signal in new[] { "FullBoxRequested", "BoxPresent", "Faulted", "CommunicationOnline", "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced" })
                Set(station, signal, signal is "BoxPresent" or "CommunicationOnline");
        foreach (var signal in new[] { "AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumperReady", "DumpingCompletedMayLeave", "Faulted" }) Set("DUMPER-001", signal, false);
    }
    public void Set(string station, string signal, bool value) => Values[(station, signal)] = value;
    public Task<PlcCycleTestEnvironment> ObserveAsync(CancellationToken ct) => Task.FromResult(new PlcCycleTestEnvironment(
        true, true, true, "map", "version", "AGV-01", Station, true, true, true, true, Healthy, DateTimeOffset.UtcNow,
        Values.Select(p => new PlcCycleTestSignal(p.Key.Item1, p.Key.Item2, p.Value, "Good", DateTimeOffset.UtcNow)).ToArray()) { RobotMap = "robot-map", RobotMapMd5 = Md5 });
    public Task<PlcCycleTestRouteProof> CheckRoutesAsync(PlcCycleTestRouteRequest r, CancellationToken ct) => Task.FromResult(new PlcCycleTestRouteProof("robot-map", Md5,
        new[] { (r.CurrentStation,r.PickupSite),(r.PickupSite,r.WaitingSite),(r.WaitingSite,r.DumpingSite),(r.DumpingSite,r.PickupSite) }
            .Where(p => p.Item1 != p.Item2).Select(p => new PlcCycleTestRoute(p.Item1,p.Item2,[p.Item1,p.Item2])).ToArray(), DateTimeOffset.UtcNow));
    public Task WriteAsync(string station, string signal, bool value, CancellationToken ct)
    {
        Writes++; if (FailWrite) throw new IOException("simulated response lost"); Set(station, signal, value); return Task.CompletedTask;
    }
}
sealed class FakeNavigation(FakePort port) : ISingleNavigationTestService
{
    public int Submits; public int Reads;
    public NavigationTestRecord? Current;
    public JackTestRecord? Jack;
    private int _jackState = 3;
    private NavigationTestPreview? _preview;
    public Task<NavigationTestStatus> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(new NavigationTestStatus(true,"AGV-01",Current,"") { Ready = true }); }
    public Task<NavigationTestStations> ReadStationsAsync(CancellationToken ct) => Task.FromResult(new NavigationTestStations("AGV-01","robot-map",FakePort.Md5,[],port.Station,DateTimeOffset.UtcNow));
    public Task<NavigationTestPreview> PreviewAsync(TrafficUserContext user, NavigationTestQuery r, CancellationToken ct)
    { _preview = new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddSeconds(30),r.VehicleId,r.SourceSite,r.TargetSite,"robot-map",FakePort.Md5,[r.SourceSite,r.TargetSite]);return Task.FromResult(_preview); }
    public Task<NavigationTestRecord> SubmitAsync(TrafficUserContext user, NavigationTestSubmit r, CancellationToken ct)
    {
        Submits++;var p=_preview!;port.Station=p.TargetSite;
        Current=new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),p.VehicleId,p.SourceSite,p.TargetSite,p.RobotMap,p.RobotMapMd5,"Completed","",DateTimeOffset.UtcNow,"test");
        return Task.FromResult(Current);
    }
    public Task<NavigationTestRecord> CancelAsync(TrafficUserContext user, NavigationTestCancel r, CancellationToken ct) => throw new NotSupportedException();
    public Task<JackTestStatus> ReadJackAsync(CancellationToken ct) => Task.FromResult(new JackTestStatus(true,null,"AGV-01",new(_jackState,true,false,0,false,0,DateTimeOffset.UtcNow),Jack));
    public Task<JackTestRecord> SubmitJackAsync(TrafficUserContext user, JackTestSubmit r, CancellationToken ct)
    { Submits++;var before=_jackState;_jackState=r.Action=="Up"?1:3;Jack=new(r.RequestId,"AGV-01",r.Action,"Completed","",before,DateTimeOffset.UtcNow,"test") { Acknowledged=true };return Task.FromResult(Jack); }
}
