using AgvDispatch.Application.Abstractions;
using AgvDispatch.Service;

var root = Path.Combine(Path.GetTempPath(), "business-run-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var user = TrafficUserContext.LocalOperation("test-operator");
BusinessRunStart Plan(string mode = "Demand", int rounds = 1) => new(Guid.NewGuid().ToString("N"), mode, "AGV-01", ["CNC-001", "CNC-002"], rounds, "LM7", true);
(BusinessRunService Service, Port Port, BusinessRunOwnership Ownership, string Path) Fixture()
{
    var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
    var port = new Port(); var ownership = new BusinessRunOwnership();
    return (new(port, ownership, path), port, ownership, path);
}
async Task<BusinessRunRecord> Row(BusinessRunService service) => (await service.ReadAsync(default)).Current!;
async Task Reject(Func<Task> operation, string label)
{ try { await operation(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or UnauthorizedAccessException) { checks++; return; } throw new Exception(label); }

var demand = Fixture(); var plan = Plan();
await demand.Service.StartAsync(user, plan, default);
Check(demand.Port.SimulatorStarts == 0 && demand.Port.Creates == 0, "demand never manufactures calls");
await demand.Service.TickAsync(default);
Check((await Row(demand.Service)).Stage == "Waiting" && demand.Port.Returns == 0, "already at standby stays idle");
demand.Port.Calls = ["CNC-001"];
await demand.Service.TickAsync(default);
Check((await Row(demand.Service)).Stage == "Executing" && demand.Port.CreatedStations.SequenceEqual(new[]{"CNC-001"}), "first call creates exactly one business task");
for (var i = 0; i < 3; i++) await demand.Service.TickAsync(default);
Check(demand.Port.Creates == 1, "running task never duplicated");
demand.Port.TaskState = "Completed"; demand.Port.Closed = false; demand.Port.Active = false;
await demand.Service.TickAsync(default);
Check((await Row(demand.Service)).CompletedJobs == 0, "task Completed with open responsibility is not a completed job");
demand.Port.Closed = true; demand.Port.Calls = ["CNC-002"];
await demand.Service.TickAsync(default); await demand.Service.TickAsync(default);
Check(demand.Port.CreatedStations.SequenceEqual(new[]{"CNC-001","CNC-002"}) && demand.Port.Returns == 0, "B accepted directly after A closure, no detour to standby");
demand.Port.TaskState = "Completed"; demand.Port.Active = false; demand.Port.Calls = []; demand.Port.Site = "LM4";
await demand.Service.TickAsync(default); await demand.Service.TickAsync(default);
Check((await Row(demand.Service)).Stage == "Returning" && demand.Port.Returns == 1 && demand.Ownership.Returning, "idle returns after closure with vehicle reservation");
demand.Port.Calls = ["CNC-001"];
await demand.Service.TickAsync(default);
Check(demand.Port.Creates == 2, "call during return does not redirect moving vehicle");
demand.Port.Returned = true; demand.Port.Site = "LM7"; demand.Port.Stopped = false;
await demand.Service.TickAsync(default);
Check(demand.Port.Releases == 0, "vendor completion without standstill does not release reservation");
demand.Port.Stopped = true;
await demand.Service.TickAsync(default); await demand.Service.TickAsync(default);
Check(demand.Port.Creates == 3 && demand.Port.Releases == 1, "call during return resumes after verified standby arrival");
await demand.Service.StartAsync(user, plan, default);
Check(demand.Port.Creates == 3, "same start request is idempotent");
await Reject(() => demand.Service.StartAsync(user, plan with { Rounds = 2 }, default), "same request mutated");

var cycle = Fixture(); await cycle.Service.StartAsync(user, Plan("Cycle"), default);
Check(cycle.Port.SimulatorStarts == 1, "cycle asks PLC tool to generate calls");
foreach (var station in new[]{"CNC-001","CNC-002"})
{
    cycle.Port.Calls = [station]; await cycle.Service.TickAsync(default);
    cycle.Port.TaskState = "Completed"; cycle.Port.Active = false; cycle.Port.Calls = [];
    await cycle.Service.TickAsync(default);
}
await cycle.Service.TickAsync(default);
Check((await Row(cycle.Service)).Stage == "Completed" && cycle.Port.Creates == 2 && cycle.Port.SimulatorStops == 1 && !cycle.Ownership.Active, "one round is A+B and closes at standby");
var old = (await Row(cycle.Service)).Plan;
await cycle.Service.StartAsync(user, Plan("Cycle"), default);
await Reject(() => cycle.Service.StartAsync(user, old, default), "old request replay while current active");

var stop = Fixture(); await stop.Service.StartAsync(user, Plan("Cycle",3), default);
stop.Port.Calls = ["CNC-001"]; await stop.Service.TickAsync(default);
await stop.Service.StopAsync(user, new((await Row(stop.Service)).RunId), default);
Check(stop.Port.SimulatorStops == 1 && (await Row(stop.Service)).Stage == "Executing", "stop only suppresses new calls, no cancellation");
stop.Port.TaskState = "Completed"; stop.Port.Active = false; stop.Port.Calls = ["CNC-002"];
await stop.Service.TickAsync(default); await stop.Service.TickAsync(default);
Check(stop.Port.Creates == 2, "already raised B call drains after stop");
stop.Port.TaskState = "Completed"; stop.Port.Active = false; stop.Port.Calls=[];
await stop.Service.TickAsync(default); await stop.Service.TickAsync(default);
Check((await Row(stop.Service)).Stage == "Completed", "stop drains remaining job then finishes");

foreach(var error in new[]{"Faulted","Cancelled","CancellationReview"})
{
    var f=Fixture(); await f.Service.StartAsync(user,Plan(),default); f.Port.Calls=["CNC-001"];await f.Service.TickAsync(default);
    f.Port.TaskState=error; await f.Service.TickAsync(default);
    Check((await Row(f.Service)).Stage=="Hold"&&f.Port.Returns==0&&f.Ownership.Returning,"abnormal task never advances: "+error);
}
var uncertain=Fixture();await uncertain.Service.StartAsync(user,Plan(),default);uncertain.Port.Site="LM4";uncertain.Port.ThrowReturn=true;
await uncertain.Service.TickAsync(default);await uncertain.Service.TickAsync(default);
Check((await Row(uncertain.Service)).Stage=="Hold"&&uncertain.Port.Returns==1,"uncertain return never resent");
var restarted=new BusinessRunService(uncertain.Port,new BusinessRunOwnership(),uncertain.Path);
await restarted.TickAsync(default);
Check((await Row(restarted)).Stage=="Hold"&&uncertain.Port.Returns==1,"restart does not resume or resend");
var changed=Fixture();await changed.Service.StartAsync(user,Plan(),default);changed.Port.Version="v2";await changed.Service.TickAsync(default);
Check((await Row(changed.Service)).Stage=="Hold"&&changed.Port.Returns==0,"map change freezes session");
var blocked=Fixture();blocked.Port.Block="恢复屏障未放行";
await Reject(()=>blocked.Service.StartAsync(user,Plan(),default),"recovery block should reject");
Check(!blocked.Ownership.Active&&blocked.Port.Creates==0,"blocked start has no effects");
await Reject(()=>blocked.Service.StartAsync(new("x","x",TrafficPermission.View),Plan(),default),"viewer should reject");
var closure=Fixture();closure.Port.Closed=false;
await Reject(()=>closure.Service.StartAsync(user,Plan(),default),"open H must reject startup");
var active=Fixture();active.Port.Active=true;
await Reject(()=>active.Service.StartAsync(user,Plan(),default),"unfinished task should reject startup");
var frozen=Fixture();await frozen.Service.StartAsync(user,Plan(),default);
var loaded=new BusinessRunService(frozen.Port,frozen.Ownership,frozen.Path);
frozen.Port.Calls=["CNC-001"];
await Reject(()=>loaded.CloseReviewedAsync(user,new((awaitedId())),default),"pending call must block review closure");
string awaitedId()=>loaded.ReadAsync(default).GetAwaiter().GetResult().Current!.RunId;
frozen.Port.Calls=[];
await loaded.CloseReviewedAsync(user,new(awaitedId()),default);
Check((await Row(loaded)).Stage=="Completed"&&!frozen.Ownership.Active,"review only closes quiet verified standby, no resume");
var reviewedAtSafeSite=Fixture();await reviewedAtSafeSite.Service.StartAsync(user,Plan(),default);
reviewedAtSafeSite.Port.Block="原任务已安全处置";await reviewedAtSafeSite.Service.TickAsync(default);
reviewedAtSafeSite.Port.Block=null;reviewedAtSafeSite.Port.Site="LM2";reviewedAtSafeSite.Port.TaskState="Cancelled";
await reviewedAtSafeSite.Service.CloseReviewedAsync(user,new((await Row(reviewedAtSafeSite.Service)).RunId),default);
Check((await Row(reviewedAtSafeSite.Service)).Stage=="Completed"&&reviewedAtSafeSite.Port.Returns==0,
    "manual review closes a quiet terminal run at a safe non-standby station without resending motion");
var outside=Fixture();await outside.Service.StartAsync(user,Plan() with{Stations=["CNC-001"]},default);
outside.Port.Site="LM4";outside.Port.Calls=["CNC-002"];await outside.Service.TickAsync(default);
Check(outside.Port.Returns==0&&outside.Port.Creates==0,"unselected call is retained and does not imply idle");
var business=AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--business-run-vehicle","AGV-01"]);
Check(business.IsBusiness&&business.ServiceArguments("test").Contains("--TaskCreation:Enabled=True")&&
    business.ServiceArguments("test").Contains("--TaskCreation:AutomaticCreationEnabled=false"),"explicit business launch enables one call coordinator");
var monitor=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([]);
var forkMode=AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--forklift-test-vehicle","AGV-02"]);
Check(forkMode.ForkTest&&forkMode.NavigationVehicle=="AGV-02"&&forkMode.ServiceArguments("fixture").Contains("--NavigationTest:ForkEnabled=True"),"forklift mode explicitly selects isolated navigation and fork profile");
Check(monitor.ServiceArguments("fixture").Contains("--NavigationTest:ForkEnabled=False"),"ordinary launch never restores fork motion permission");
await Reject(()=>Task.FromResult(AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--forklift-test-vehicle","AGV-02","--business-run-vehicle","AGV-01"])),"forklift and business mode cannot coexist");
await Reject(()=>Task.FromResult(AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--forklift-test-vehicle","AGV-02","--navigation-test-vehicle","AGV-01"])),"forklift and chassis-jack navigation mode cannot be combined");
Check(!monitor.IsBusiness&&monitor.ServiceArguments("test").Contains("--Plc:AllowWrites=False"),"default launch never restores send permission");
await Reject(()=>Task.FromResult(AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--business-run-vehicle","AGV-01","--navigation-test-vehicle","AGV-01"])),"isolated and business mode cannot coexist");
var badOptions = new ManualTaskCreationOptions("map", "v1", false, "blocked", "DUMPER-001", "LM5", "LM6",
    [new("CNC-001", "LM2", false, "第7步：缺少 DumperExit.ResourceIds；缺少 DumperExit.BodyProfiles"),
     new("CNC-002", "LM4", false, "第7步：其他业务动作参数缺失")], []);
var explanation = BusinessRunPort.ConfigurationError(badOptions, ["CNC-001", "CNC-002"])!;
Check(explanation.Contains("DumperExit.ResourceIds") && explanation.Contains("DumperExit.BodyProfiles") && explanation.Contains("Service/appsettings.json"), "configuration rejection retains exact missing fields and actual configuration location");
Check(explanation.Contains("CNC-002：第7步：其他业务动作参数缺失"), "all selected station configuration errors are reported together");
Check(!BusinessRunPort.ConfigurationError(badOptions, ["CNC-002"])!.Contains("CNC-001"), "unselected station errors are excluded");
Check(BusinessRunPort.ConfigurationError(badOptions, ["CNC-999"])!.Contains("未配置该工位"), "missing station mapping is explained explicitly");
var validPreview = Enumerable.Range(1, 9).Select(i => new ManualTaskStepPreview(i, default, "step", "", "", "", "", "", "", "")).ToArray();
var noCall = badOptions with { Stations = [new("CNC-001", "LM2", false, "尚无PLC呼叫") { PreviewSteps = validPreview }] };
Check(BusinessRunPort.ConfigurationError(noCall, ["CNC-001"]) is null, "waiting for a PLC call is not misreported as missing configuration");
var badVehicle = noCall with { Vehicles = [new("AGV-01", "AGV-01", "LM1", false, "28阶段出区配置：未核定整车载荷包络。")] };
Check(BusinessRunPort.VehicleConfigurationError(badVehicle, "AGV-01")!.Contains("未核定整车载荷包络"), "vehicle exit configuration is rejected before simulator calls start");
Check(BusinessRunPort.VehicleConfigurationError(badVehicle, "MISSING")!.Contains("未配置"), "missing selected vehicle is explicit");
var ownedVehicle = noCall with { Vehicles = [new("AGV-01", "AGV-01", "LM7", false, "在线空闲，定位正常。")] };
Check(BusinessRunPort.VehicleConfigurationError(ownedVehicle, "AGV-01") is null, "run ownership is not mistaken for an exit configuration failure during reviewed closure");
var retirementPath = Path.Combine(root, "retirements.json");
var owner = new BusinessRunOwnership(); owner.LoadRetirements(retirementPath);
var pendingId = Guid.NewGuid().ToString("N");
var pendingRun = new BusinessRunRecord(Guid.NewGuid().ToString("N"), Plan(), "map", "v1", "revision", "operator",
    "Hold", "rejected", 0, null, pendingId, null, true, DateTimeOffset.UtcNow);
owner.Retire(pendingRun);
var restoredOwner = new BusinessRunOwnership(); restoredOwner.LoadRetirements(retirementPath);
Check(restoredOwner.IsRetired(pendingId), "retired request fence survives a restart");
restoredOwner.Retire(pendingRun);
Check(System.Text.Json.JsonSerializer.Deserialize<BusinessRunOwnership.Retirement[]>(File.ReadAllText(retirementPath))!.Length == 1,
    "review retry does not duplicate or discard original request audit");
var damaged = Path.Combine(root, "damaged.json"); File.WriteAllText(damaged, "null");
try { new BusinessRunOwnership().LoadRetirements(damaged); throw new Exception("corrupt fence accepted"); }
catch (InvalidDataException) { checks++; }
var retirementGate = new AgvDispatch.Infrastructure.Maps.MapExecutionGate();
retirementGate.SetMode(MapExecutionMode.Running);
var receiptStore = new ReceiptStore();
var chains = new BusinessStandbyChains(Path.Combine(root, "chains"));
var realPort = new BusinessRunPort(null!, null!, null!, null!, null!, retirementGate, null!, null!, null!, null!, null!,
    null!, null!, null!, null!, chains, null!, null!, true, receiptStore, owner);
var acceptedId = Guid.NewGuid().ToString("N");
var acceptedRun = pendingRun with { PendingRequestId = acceptedId };
using (var heldGate = await retirementGate.EnterControlAsync(default))
{
    var review = realPort.RetireUnacceptedAsync(acceptedRun, default);
    Check(!review.IsCompleted, "review serializes with in-flight task creation on the execution gate");
    receiptStore.Receipt = new("task", acceptedId, "CNC-001", "AGV-01", 50, "map", "v1") { ParameterRevision = "revision" };
    heldGate.Dispose();
    await Reject(async () => await review, "accepted original must never be retired");
}
Check(!owner.IsRetired(acceptedId), "in-flight accepted receipt wins over request retirement");
receiptStore.Receipt = null;
var chainId = Guid.NewGuid().ToString("N");
chains.Save(new("STANDBY-" + chainId, "AGV-01", ["LM1", "LM7"], 1, true));
await Reject(async () => await realPort.RetireUnacceptedAsync(pendingRun with { PendingRequestId = chainId }, default), "existing standby intent must remain uncertain");
Check(!owner.IsRetired(chainId), "existing send chain is never fenced as an unaccepted request");
var unacceptedId = Guid.NewGuid().ToString("N");
var reviewed = await realPort.RetireUnacceptedAsync(pendingRun with { PendingRequestId = unacceptedId }, default);
Check(reviewed.PendingRequestId is null && owner.IsRetired(unacceptedId), "only absent receipt and absent send chain produce a durable fence");
var resuming=Fixture();await resuming.Service.StartAsync(user,Plan(),default);resuming.Port.Calls=["CNC-001"];await resuming.Service.TickAsync(default);
resuming.Port.Block="车辆状态过期";await resuming.Service.TickAsync(default);
var held=await Row(resuming.Service);
var resumeRequest=new BusinessRunResume(held.RunId,Guid.NewGuid().ToString("N"),held.UpdatedAt,"已现场核对车辆定位和原任务载荷",true);
await Reject(()=>resuming.Service.ResumeAsync(user,resumeRequest,default),"health block must remain");
resuming.Port.Block=null;
await Reject(()=>resuming.Service.ResumeAsync(user,resumeRequest with { ExpectedUpdatedAt=held.UpdatedAt.AddSeconds(-1) },default),"stale resume version");
resuming.Port.TaskState="CancellationReview";
await Reject(()=>resuming.Service.ResumeAsync(user,resumeRequest,default),"resume cannot replace cancelled task review");
resuming.Port.TaskState="Executing";
var resumed=(await resuming.Service.ResumeAsync(user,resumeRequest,default)).Current!;
Check(resumed.Stage=="Executing"&&resumed.TaskId==held.TaskId&&resumed.PendingRequestId==held.PendingRequestId&&resuming.Port.Creates==1,"resume only attaches original accepted task");
await resuming.Service.ResumeAsync(user,resumeRequest,default);
Check(resuming.Port.Creates==1&&resuming.Port.Returns==0,"same resume request never creates or resends motion");
await Reject(()=>resuming.Service.ResumeAsync(user,resumeRequest with { Reason="相同编号不允许更换现场核对依据" },default),"mutated resume rejected");
var uncertainRow=await Row(uncertain.Service);
await Reject(()=>uncertain.Service.ResumeAsync(user,new(uncertainRow.RunId,Guid.NewGuid().ToString("N"),uncertainRow.UpdatedAt,"已查看原记录但发送结果仍然不明",true),default),"unknown original send remains fenced");
var resumeStop=Fixture();await resumeStop.Service.StartAsync(user,Plan(),default);resumeStop.Port.Block="offline";await resumeStop.Service.TickAsync(default);
await resumeStop.Service.StopAsync(user,new((await Row(resumeStop.Service)).RunId),default);resumeStop.Port.Block=null;
var stoppedHold=await Row(resumeStop.Service);
var stoppedResume=(await resumeStop.Service.ResumeAsync(user,new(stoppedHold.RunId,Guid.NewGuid().ToString("N"),stoppedHold.UpdatedAt,"已现场确认空闲停靠且只继续收尾",true),default)).Current!;
Check(stoppedResume.StopRequested&&stoppedResume.Stage=="Waiting","resume preserves stop-new-calls intent");
// Deployment identifiers and station cardinality must come from configuration.
var custom = Fixture(); custom.Port.Site = "PARK-NORTH";
string[] customStations = ["LATHE-037", "MILL-WEST", "PRESS-9"];
var customPlan = Plan("Cycle", 2) with { VehicleId = "CARRIER-007", Stations = customStations, StandbySite = "PARK-NORTH" };
await custom.Service.StartAsync(user, customPlan, default);
foreach (var station in customStations.Concat(customStations))
{
    custom.Port.Calls = [station]; await custom.Service.TickAsync(default);
    custom.Port.TaskState = "Completed"; custom.Port.Active = false; custom.Port.Calls = [];
    await custom.Service.TickAsync(default);
}
await custom.Service.TickAsync(default);
Check(custom.Port.CreatedStations.SequenceEqual(customStations.Concat(customStations)) &&
    (await Row(custom.Service)).Stage == "Completed", "custom three-station two-round run preserves configured order and standby");
var scale = Fixture();
var scalePlan = Plan() with { Stations = Enumerable.Range(1, 300).Select(i => $"LINE-{i:000}").ToArray() };
await scale.Service.StartAsync(user, scalePlan, default);
Check((await Row(scale.Service)).Plan.Stations.Length == 300, "300 configured workstations accepted without sample identifiers");
await Reject(() => Fixture().Service.StartAsync(user, Plan() with { Stations = ["CELL-X", "cell-x"] }, default), "case-insensitive duplicate rejected");
await Reject(() => Fixture().Service.StartAsync(user, Plan() with { Stations = [" "] }, default), "blank workstation rejected");
checks += FeedbackTests.Run();
Console.WriteLine($"BusinessRunSmokeTest passed {checks} checks. Fake ports only; no PLC or AGV commands.");

sealed class ReceiptStore : IManualTaskCreationRepository
{
    public ManualTaskReceipt? Receipt;
    public Task<ManualTaskReceipt?> FindRequestAsync(string id, CancellationToken ct) => Task.FromResult(Receipt);
    public Task<AgvDispatch.Domain.Tasks.TaskCreationResult> TryCreateManualAsync(ManualTaskCreationRequest r, string d, string o, CancellationToken ct)
        => throw new Exception("retirement must never create tasks");
}

sealed class Port : IBusinessRunPort
{
    public string[] Calls=[];public bool Closed=true,Active,Stopped=true,Returned,ThrowReturn;
    public string Site="LM7",Version="v1";public string? TaskState,Block;
    public int Creates,Returns,Releases,SimulatorStarts,SimulatorStops;public List<string> CreatedStations=[];
    public Task ValidateAsync(BusinessRunStart plan,CancellationToken ct)=>Task.CompletedTask;
    public Task<BusinessRunObservation> ObserveAsync(BusinessRunRecord run,CancellationToken ct)=>Task.FromResult(new BusinessRunObservation("map",Version,"revision",Block,Calls,Closed,Active,Site,Stopped,TaskState,Returned));
    public Task<string> CreateAsync(BusinessRunRecord run,string station,string id,CancellationToken ct)
    {Creates++;CreatedStations.Add(station);Active=true;TaskState="Executing";return Task.FromResult("task-"+Creates);}
    public Task<string> ReturnAsync(BusinessRunRecord run,CancellationToken ct)
    {Returns++;if(ThrowReturn)throw new IOException("response lost");return Task.FromResult("vendor-return");}
    public Task SimulatorAsync(BusinessRunRecord run,bool stop,CancellationToken ct)
    {if(stop)SimulatorStops++;else SimulatorStarts++;return Task.CompletedTask;}
    public Task ReleaseReturnAsync(BusinessRunRecord run,CancellationToken ct){Releases++;return Task.CompletedTask;}
}
