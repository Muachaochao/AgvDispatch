using AgvDispatch.Application.Abstractions;
using AgvDispatch.Service;

var root = Path.Combine(Path.GetTempPath(), "business-run-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var user = TrafficUserContext.LocalOperation("test-operator");
BusinessRunStart Plan(string mode = "Demand", int rounds = 1) => new(Guid.NewGuid().ToString("N"), mode, "AGV-01", ["CNC-001", "CNC-002"], rounds, "LM7", true);
(BusinessRunService Service, Port Port, BusinessRunOwnership Ownership, string Path) Fixture(
    IChargingDispatchGuard? charging = null, ITaskSwitchGuard? switchGuard = null)
{
    var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
    var port = new Port(); var ownership = new BusinessRunOwnership();
    return (new(port, ownership, path, charging, switchGuard), port, ownership, path);
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
cycle.Port.Site = "LM4";
await cycle.Service.TickAsync(default);
Check(cycle.Port.Returns == 0 && (await Row(cycle.Service)).Stage == "Waiting",
    "cycle waits briefly for the first simulated call before returning to standby");
cycle.Port.Site = "LM7";
foreach (var station in new[]{"CNC-001","CNC-002"})
{
    cycle.Port.Calls = [station]; await cycle.Service.TickAsync(default);
    cycle.Port.TaskState = "Completed"; cycle.Port.Active = false; cycle.Port.Calls = [];
    await cycle.Service.TickAsync(default);
}
await cycle.Service.TickAsync(default);
Check((await Row(cycle.Service)).Stage == "Completed" && cycle.Port.Creates == 2 && cycle.Port.SimulatorStops == 1 && !cycle.Ownership.Active, "one round is A+B and closes at standby");
var betweenCalls = Fixture(); await betweenCalls.Service.StartAsync(user, Plan("Cycle", 2) with { Stations = ["CNC-001"] }, default);
betweenCalls.Port.Calls = ["CNC-001"]; await betweenCalls.Service.TickAsync(default);
betweenCalls.Port.TaskState = "Completed"; betweenCalls.Port.Active = false; betweenCalls.Port.Calls = [];
await betweenCalls.Service.TickAsync(default);
Check((await Row(betweenCalls.Service)).Stage == "Waiting" && betweenCalls.Port.Creates == 1,
    "cycle leaves a brief second-call window before deciding no work remains");
var old = (await Row(cycle.Service)).Plan;
await cycle.Service.StartAsync(user, Plan("Cycle"), default);
await Reject(() => cycle.Service.StartAsync(user, old, default), "old request replay while current active");

var switchGuard = new FixedSwitchGuard(true);
var switchHold = Fixture(switchGuard: switchGuard);
await switchHold.Service.StartAsync(user, Plan(), default);
switchHold.Port.Calls = ["CNC-001"]; await switchHold.Service.TickAsync(default);
switchGuard.Allowed = false;
switchHold.Port.TaskState = "Completed"; switchHold.Port.Active = false; switchHold.Port.Closed = true;
switchHold.Port.Calls = ["CNC-002"];
await switchHold.Service.TickAsync(default); await switchHold.Service.TickAsync(default);
var switchState = await Row(switchHold.Service);
Check(switchState.Stage == "Hold" && switchHold.Port.Creates == 1,
    $"active switch pauses continuous run before creating the next station task; actual={switchState.Stage}/{switchHold.Port.Creates}");

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
var autoDetour=AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--navigation-test-vehicle","AGV-02","--auto-navigation-detour"]);
Check(forkMode.ForkTest&&forkMode.NavigationVehicle=="AGV-02"&&forkMode.ServiceArguments("fixture").Contains("--NavigationTest:ForkEnabled=True"),"forklift mode explicitly selects isolated navigation and fork profile");
Check(monitor.ServiceArguments("fixture").Contains("--NavigationTest:ForkEnabled=False"),"ordinary launch never restores fork motion permission");
Check(autoDetour.AutoNavigationDetour&&autoDetour.ServiceArguments("fixture").Contains("--NavigationTest:AutoDetourEnabled=True"),
    "automatic detour permission requires and records the explicit single-vehicle launch flag");
Check(!monitor.AutoNavigationDetour&&monitor.ServiceArguments("fixture").Contains("--NavigationTest:AutoDetourEnabled=False"),
    "ordinary launch never restores automatic cancellation or detour permission");
string[] dailyDefaults=["--default-navigation-test-vehicle","AGV-02","--default-auto-navigation-detour"];
var daily=AgvDispatch.WebHost.WebLocalLaunchMode.Parse(dailyDefaults);
Check(daily.NavigationVehicle=="AGV-02"&&daily.AutoNavigationDetour,"daily single-vehicle selection is preserved when no mode is explicitly selected");
var dailyFleet=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([..dailyDefaults,"--fleet-test-vehicles","AGV-01,AGV-03"]);
Check(dailyFleet.FleetVehicles.SequenceEqual(new[]{"AGV-01","AGV-03"})&&!dailyFleet.AutoNavigationDetour&&dailyFleet.NavigationVehicle.Length==0,
    "fleet entry replaces daily navigation defaults without inheriting automatic detour permission");
var dailyBusiness=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([..dailyDefaults,"--business-run-vehicle","AGV-01"]);
Check(dailyBusiness.IsBusiness&&!dailyBusiness.AutoNavigationDetour&&dailyBusiness.NavigationVehicle.Length==0,"business entry replaces daily test defaults");
var dailyCycle=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([..dailyDefaults,"--plc-cycle-test-vehicle","AGV-03","--plc-cycle-test-station","CNC-001"]);
Check(dailyCycle.IsPlcCycle&&dailyCycle.NavigationVehicle=="AGV-03"&&!dailyCycle.AutoNavigationDetour,"PLC cycle entry replaces daily navigation defaults");
var dailyOther=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([..dailyDefaults,"--forklift-test-vehicle","AGV-03"]);
Check(dailyOther.ForkTest&&dailyOther.NavigationVehicle=="AGV-03"&&!dailyOther.AutoNavigationDetour,"explicit forklift selection never inherits default automatic detour");
var dailyManual=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([..dailyDefaults,"--navigation-test-vehicle","AGV-01"]);
Check(dailyManual.NavigationVehicle=="AGV-01"&&!dailyManual.AutoNavigationDetour,"explicit manual navigation replaces default vehicle and automatic permission");
await Reject(()=>Task.FromResult(AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--default-auto-navigation-detour"])),"orphan default automatic flag must be rejected");
await Reject(()=>Task.FromResult(AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--auto-navigation-detour"])),
    "automatic detour cannot run without an explicit test vehicle");
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
Check(BusinessRunPort.IsRetryableCreationBlock("暂无满足 PLC 条件和路径要求的工位") &&
    BusinessRunPort.IsRetryableCreationBlock("PLC条件尚未满足。") &&
    !BusinessRunPort.IsRetryableCreationBlock("工位故障或通讯无效：CNC-001"),
    "only transient station-state readiness blocks are retried before task creation");
Check(BusinessRunPort.IsLocalTrafficObstacle(new("52200", AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error, "robot is blocked")) &&
    !BusinessRunPort.IsLocalTrafficObstacle(new("52200", AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Fatal, "fatal blocked")) &&
    !BusinessRunPort.IsLocalTrafficObstacle(new("51001", AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error, "motor fault")),
    "local obstacle alarms are debounced without hiding unrelated vehicle faults");
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
LocalizationChecks.Run(Check);
await ObservationChecks.RunAsync(Check);
var lowAtCompletion = Fixture();
await lowAtCompletion.Service.StartAsync(user, Plan("Cycle"), default);
lowAtCompletion.Port.Calls = ["CNC-001"]; await lowAtCompletion.Service.TickAsync(default);
lowAtCompletion.Port.TaskState = "Completed"; lowAtCompletion.Port.Active = false;
lowAtCompletion.Port.Calls = []; lowAtCompletion.Port.Stopped = false;
await lowAtCompletion.Service.TickAsync(default); await lowAtCompletion.Service.TickAsync(default);
Check((await Row(lowAtCompletion.Service)) is { Stage: "Executing", CompletedJobs: 0 },
    "completion with unverified localization/standstill waits without counting or holding the run");
lowAtCompletion.Port.Stopped = true; await lowAtCompletion.Service.TickAsync(default);
Check((await Row(lowAtCompletion.Service)) is { Stage: "Waiting", CompletedJobs: 1 },
    "fresh verified standstill permits original completion without manual recovery");

var chargeGuard = new ChargingGuard();
var chargingRun = Fixture(chargeGuard);
var chargingPlan = Plan("Cycle", 3);
await chargingRun.Service.StartAsync(user, chargingPlan, default);
chargingRun.Port.Calls = ["CNC-001"]; await chargingRun.Service.TickAsync(default);
chargeGuard.Allow = false;
await chargingRun.Service.TickAsync(default);
Check((await Row(chargingRun.Service)).Stage == "Executing" && chargingRun.Ownership.Active && chargingRun.Port.SimulatorStops == 0,
    "low battery does not interrupt the accepted full-box cycle");
chargingRun.Port.TaskState = "Completed"; chargingRun.Port.Active = false; chargingRun.Port.Closed = false;
await chargingRun.Service.TickAsync(default);
Check((await Row(chargingRun.Service)).CompletedJobs == 0, "charging waits for equipment responsibility as well as business completion");
chargingRun.Port.Closed = true; chargingRun.Port.Calls = ["CNC-002"];
await chargingRun.Service.TickAsync(default); await chargingRun.Service.TickAsync(default);
var pausedForCharge = await Row(chargingRun.Service);
Check(pausedForCharge is { Stage: "WaitingForCharge", CompletedJobs: 1, ChargeResumePending: true, ChargeSimulatorPaused: true } &&
    pausedForCharge.Plan == chargingPlan && pausedForCharge.TaskId is null && !chargingRun.Ownership.Active && !chargingRun.Ownership.Returning,
    "safe charging handoff durably retains the exact original plan and progress without business ownership");
Check(chargingRun.Ownership.GetChargingReturnSite("agv-01") == "LM7" && chargingRun.Ownership.GetChargingReturnSite("other") is null &&
    System.Text.Json.JsonSerializer.Deserialize<BusinessRunRecord>(File.ReadAllText(chargingRun.Path))!.Stage == "WaitingForCharge",
    "charging return destination is vehicle scoped and the wait survives on disk");
for (var i = 0; i < 3; i++) await chargingRun.Service.TickAsync(default);
Check(chargingRun.Port.Creates == 1 && chargingRun.Port.Returns == 0 && chargingRun.Port.SimulatorStops == 1 && chargingRun.Port.Calls.SequenceEqual(new[] { "CNC-002" }),
    "charging wait never creates business, standby, repeated simulator stops or clears latched calls");
var pausedRestartOwner = new BusinessRunOwnership();
var pausedRestart = new BusinessRunService(chargingRun.Port, pausedRestartOwner, chargingRun.Path, chargeGuard);
await pausedRestart.TickAsync(default);
Check((await Row(pausedRestart)).Stage == "Hold" && pausedRestartOwner.Active && pausedRestartOwner.GetChargingReturnSite("AGV-01") == "LM7" && chargingRun.Port.SimulatorStarts == 1,
    "restart during charging preserves destination and holds instead of resuming the plan");
chargeGuard.Allow = true; chargeGuard.Owns = true;
await chargingRun.Service.TickAsync(default);
Check((await Row(chargingRun.Service)).Stage == "WaitingForCharge", "charging ownership must end even if the battery gate permits new business");
chargeGuard.Owns = false; chargingRun.Port.Site = "CHARGE-EXIT";
await chargingRun.Service.TickAsync(default);
Check((await Row(chargingRun.Service)).Stage == "WaitingForCharge", "charging exit alone does not substitute for the original standby destination");
chargingRun.Port.Site = "LM7"; chargingRun.Port.Stopped = false;
await chargingRun.Service.TickAsync(default);
chargingRun.Port.Stopped = true; chargingRun.Port.Closed = false;
await chargingRun.Service.TickAsync(default);
chargingRun.Port.Closed = true; chargingRun.Port.Active = true;
await chargingRun.Service.TickAsync(default);
Check(chargingRun.Port.SimulatorStarts == 1 && chargingRun.Port.Creates == 1, "moving vehicle, open handshake or active tasks each prevent charge resumption");
chargingRun.Port.Active = false;
chargingRun.Port.AfterValidate = () => chargingRun.Port.Stopped = false;
await chargingRun.Service.TickAsync(default);
Check((await Row(chargingRun.Service)).Stage == "WaitingForCharge" && chargingRun.Port.SimulatorStarts == 1,
    "a stop-state change during plan validation prevents resumption");
chargingRun.Port.AfterValidate = null; chargingRun.Port.Stopped = true;
await chargingRun.Service.TickAsync(default);
Check((await Row(chargingRun.Service)) is { Stage: "Waiting", CompletedJobs: 1, ChargeResumePending: false } &&
    chargingRun.Ownership.Active && chargingRun.Ownership.GetChargingReturnSite("AGV-01") is null &&
    chargingRun.Port.SimulatorStarts == 2 && chargingRun.Port.LastSimulatorStartStage == "ResumingAfterCharge" &&
    chargingRun.Port.LastSimulatorRunId == pausedForCharge.RunId && chargingRun.Port.Creates == 1,
    "charging resumes the same validated plan and simulator once, without creating a business task in the recovery phase");
await chargingRun.Service.TickAsync(default);
Check(chargingRun.Port.CreatedStations.SequenceEqual(new[] { "CNC-001", "CNC-002" }) && (await Row(chargingRun.Service)).CompletedJobs == 1,
    "retained call resumes after charging without resetting completed rounds");

var stopChargeGuard = new ChargingGuard(); var stopDuringCharge = Fixture(stopChargeGuard);
await stopDuringCharge.Service.StartAsync(user, Plan("Cycle", 2), default);
stopChargeGuard.Allow = false; await stopDuringCharge.Service.TickAsync(default);
stopDuringCharge.Port.Calls = ["CNC-001"];
await stopDuringCharge.Service.StopAsync(user, new((await Row(stopDuringCharge.Service)).RunId), default);
await stopDuringCharge.Service.TickAsync(default);
Check((await Row(stopDuringCharge.Service)) is { Stage: "WaitingForCharge", StopRequested: true } &&
    stopDuringCharge.Ownership.GetChargingReturnSite("AGV-01") == "LM7" && stopDuringCharge.Port.Creates == 0 && stopDuringCharge.Port.Returns == 0,
    "stop during charging retains return destination and never cancels charging or sends standby");
stopChargeGuard.Allow = true; await stopDuringCharge.Service.TickAsync(default);
Check((await Row(stopDuringCharge.Service)).Stage == "Completed" && !stopDuringCharge.Ownership.Active &&
    stopDuringCharge.Ownership.GetChargingReturnSite("AGV-01") is null && stopDuringCharge.Port.SimulatorStarts == 1 &&
    stopDuringCharge.Port.Creates == 0 && stopDuringCharge.Port.Calls.SequenceEqual(new[] { "CNC-001" }),
    "stopped business wait completes after verified charging return without restarting calls or clearing latched requests");

var changedChargeGuard = new ChargingGuard(); var changedAfterCharge = Fixture(changedChargeGuard);
await changedAfterCharge.Service.StartAsync(user, Plan(), default);
changedChargeGuard.Allow = false; await changedAfterCharge.Service.TickAsync(default);
changedChargeGuard.Allow = true; changedAfterCharge.Port.Version = "v2";
await changedAfterCharge.Service.TickAsync(default); await changedAfterCharge.Service.TickAsync(default);
Check((await Row(changedAfterCharge.Service)).Stage == "Hold" && changedAfterCharge.Port.Creates == 0 && changedAfterCharge.Port.Returns == 0,
    "map change while charging holds the retained plan rather than automatically rebinding it");

var invalidChargeGuard = new ChargingGuard(); var invalidAfterCharge = Fixture(invalidChargeGuard);
await invalidAfterCharge.Service.StartAsync(user, Plan(), default);
invalidChargeGuard.Allow = false; await invalidAfterCharge.Service.TickAsync(default);
invalidChargeGuard.Allow = true; invalidAfterCharge.Port.ValidationBlock = "vehicle pool changed";
await invalidAfterCharge.Service.TickAsync(default);
Check((await Row(invalidAfterCharge.Service)).Stage == "Hold" && invalidAfterCharge.Port.Creates == 0 && invalidAfterCharge.Port.Validations == 2,
    "original plan configuration is revalidated before business can resume after charging");

var uncertainChargeGuard = new ChargingGuard(); var uncertainChargeResume = Fixture(uncertainChargeGuard);
await uncertainChargeResume.Service.StartAsync(user, Plan("Cycle", 2), default);
uncertainChargeGuard.Allow = false; await uncertainChargeResume.Service.TickAsync(default);
uncertainChargeGuard.Allow = true; uncertainChargeResume.Port.ThrowSimulatorStart = true;
await uncertainChargeResume.Service.TickAsync(default); await uncertainChargeResume.Service.TickAsync(default);
var unknownChargeResume = await Row(uncertainChargeResume.Service);
Check(unknownChargeResume is { Stage: "Hold", ChargeResumePending: true, ChargeSimulatorResumeAttempted: true } &&
    uncertainChargeResume.Port.SimulatorStarts == 2 && uncertainChargeResume.Port.Creates == 0,
    "unknown simulator resume is durably held and never automatically repeated");
await Reject(() => uncertainChargeResume.Service.ResumeAsync(user,
    new(unknownChargeResume.RunId, Guid.NewGuid().ToString("N"), unknownChargeResume.UpdatedAt, "已现场检查但原模拟恢复应答仍然未知", true), default),
    "manual resume must not replay an uncertain simulator start");
await uncertainChargeResume.Service.StopAsync(user, new(unknownChargeResume.RunId), default);
var stoppedChargeHold = await Row(uncertainChargeResume.Service);
await uncertainChargeResume.Service.ResumeAsync(user,
    new(stoppedChargeHold.RunId, Guid.NewGuid().ToString("N"), stoppedChargeHold.UpdatedAt, "已确认模拟器停止且充电完成返回原待机点", true), default);
await uncertainChargeResume.Service.TickAsync(default);
Check((await Row(uncertainChargeResume.Service)).Stage == "Completed" && uncertainChargeResume.Port.SimulatorStarts == 2,
    "explicit confirmed stop can close a reviewed charging wait without replaying its uncertain resume");
Console.WriteLine($"BusinessRunSmokeTest passed {checks} checks. Fake ports only; no PLC or AGV commands.");

sealed class ReceiptStore : IManualTaskCreationRepository
{
    public ManualTaskReceipt? Receipt;
    public Task<ManualTaskReceipt?> FindRequestAsync(string id, CancellationToken ct) => Task.FromResult(Receipt);
    public Task<AgvDispatch.Domain.Tasks.TaskCreationResult> TryCreateManualAsync(ManualTaskCreationRequest r, string d, string o, CancellationToken ct)
        => throw new Exception("retirement must never create tasks");
}

sealed class FixedSwitchGuard(bool allowed) : ITaskSwitchGuard
{
    public bool Allowed { get; set; } = allowed;
    public Task<bool> AllowsVehicleAsync(string vehicleId, string? taskId, string? requestId, CancellationToken ct) =>
        Task.FromResult(Allowed);
}

sealed class Port : IBusinessRunPort
{
    public string[] Calls=[];public bool Closed=true,Active,Stopped=true,Returned,ThrowReturn;
    public string Site="LM7",Version="v1";public string? TaskState,Block;
    public int Creates,Returns,Releases,SimulatorStarts,SimulatorStops,Validations;public List<string> CreatedStations=[];
    public bool ThrowSimulatorStart; public string? ValidationBlock, LastSimulatorStartStage, LastSimulatorRunId;
    public Action? AfterValidate;
    public Task ValidateAsync(BusinessRunStart plan,CancellationToken ct)
    {Validations++;if(ValidationBlock is not null)throw new InvalidOperationException(ValidationBlock);AfterValidate?.Invoke();return Task.CompletedTask;}
    public Task<BusinessRunObservation> ObserveAsync(BusinessRunRecord run,CancellationToken ct)=>Task.FromResult(new BusinessRunObservation("map",Version,"revision",Block,Calls,Closed,Active,Site,Stopped,TaskState,Returned));
    public Task<string> CreateAsync(BusinessRunRecord run,string station,string id,CancellationToken ct)
    {Creates++;CreatedStations.Add(station);Active=true;TaskState="Executing";return Task.FromResult("task-"+Creates);}
    public Task<string> ReturnAsync(BusinessRunRecord run,CancellationToken ct)
    {Returns++;if(ThrowReturn)throw new IOException("response lost");return Task.FromResult("vendor-return");}
    public Task SimulatorAsync(BusinessRunRecord run,bool stop,CancellationToken ct)
    {if(stop)SimulatorStops++;else{SimulatorStarts++;LastSimulatorStartStage=run.Stage;LastSimulatorRunId=run.RunId;if(ThrowSimulatorStart)throw new IOException("simulator response lost");}return Task.CompletedTask;}
    public Task ReleaseReturnAsync(BusinessRunRecord run,CancellationToken ct){Releases++;return Task.CompletedTask;}
}

sealed class ChargingGuard : IChargingDispatchGuard
{
    public bool Allow = true, Owns;
    public bool HasActiveSessions => Owns;
    public bool OwnsVehicle(string vehicleId) => Owns;
    public bool AllowsNewBusiness(string vehicleId) => Allow;
    public ValueTask<IDisposable> EnterBusinessAsync(CancellationToken ct) => ValueTask.FromResult<IDisposable>(new Lease());
    private sealed class Lease : IDisposable { public void Dispose() { } }
}
