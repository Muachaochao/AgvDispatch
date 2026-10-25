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
var outside=Fixture();await outside.Service.StartAsync(user,Plan() with{Stations=["CNC-001"]},default);
outside.Port.Site="LM4";outside.Port.Calls=["CNC-002"];await outside.Service.TickAsync(default);
Check(outside.Port.Returns==0&&outside.Port.Creates==0,"unselected call is retained and does not imply idle");
var business=AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--business-run-vehicle","AGV-01"]);
Check(business.IsBusiness&&business.ServiceArguments("test").Contains("--TaskCreation:Enabled=True")&&
    business.ServiceArguments("test").Contains("--TaskCreation:AutomaticCreationEnabled=false"),"explicit business launch enables one call coordinator");
var monitor=AgvDispatch.WebHost.WebLocalLaunchMode.Parse([]);
Check(!monitor.IsBusiness&&monitor.ServiceArguments("test").Contains("--Plc:AllowWrites=False"),"default launch never restores send permission");
await Reject(()=>Task.FromResult(AgvDispatch.WebHost.WebLocalLaunchMode.Parse(["--business-run-vehicle","AGV-01","--navigation-test-vehicle","AGV-01"])),"isolated and business mode cannot coexist");
Console.WriteLine($"BusinessRunSmokeTest passed {checks} checks. Fake ports only; no PLC or AGV commands.");

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
