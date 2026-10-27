using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;
using AgvDispatch.WebHost;

var root=Path.Combine(Path.GetTempPath(),"fleet-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var count=0;
void Check(bool condition,string text){if(!condition)throw new Exception(text);count++;}
async Task Reject(Func<Task> operation,string text){try{await operation();}catch(InvalidOperationException){count++;return;}catch(ArgumentException){count++;return;}throw new Exception(text);}
var user=TrafficUserContext.LocalOperation("fleet-test");
FleetTestStart Plan(string scenario)=>new(Guid.NewGuid().ToString("N"),scenario,["AGV-01","AGV-02"],true);
(FleetTestService Service,Port Port,FleetTestOwnership Owner,string Path) Fixture(bool enabled=true)
{var p=new Port();var o=new FleetTestOwnership(new(){Enabled=enabled,VehicleIds=["AGV-01","AGV-02"]});var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".json");return(new(p,o,path),p,o,path);}
foreach(var scenario in new[]{"Staggered","Simultaneous","Handover"})
{
 var f=Fixture();var plan=Plan(scenario);await f.Service.StartAsync(user,plan,default);await f.Service.StartAsync(user,plan,default);
 Check(f.Port.Starts==1,"原申请幂等");await f.Service.TickAsync(default);
 var state=(await f.Service.ReadAsync(default)).Current!;
 Check(f.Port.Calls.Count==(scenario=="Simultaneous"?2:1),"第一批呼叫数量");
 Check(f.Port.Creates==f.Port.Calls.Count,"每工位仅建一单");
 Check(f.Owner.Allows(state.Calls[0].TaskId!,"AGV-02")&&!f.Owner.Allows(state.Calls[0].TaskId!,"AGV-03")&&!f.Owner.Allows("foreign","AGV-01"),"分车无工位固定绑定且拒绝范围外车/任务");
 if(scenario!="Simultaneous")
 {
  await f.Service.TickAsync(default);Check(f.Port.Calls.Count==1,"条件未到不提前发后车呼叫");
  f.Port.Milestone(scenario=="Staggered"?DispatchTaskStepType.PickupFullBox:DispatchTaskStepType.DumpFullBox);
  await f.Service.TickAsync(default);Check(f.Port.Calls.Count==2,"按取箱/倒料里程碑触发第二工位");
 }
 Check(f.Port.Creates==2,"双车用例仅两笔任务");
 await f.Service.TickAsync(default);Check(f.Port.Creates==2,"重复轮询无重复建单");
 f.Port.Bad=true;await f.Service.TickAsync(default);Check((await f.Service.ReadAsync(default)).Current!.Stage=="Hold"&&!f.Owner.Allows(state.Calls[0].TaskId!,"AGV-01"),"任一车数据失效暂停新增与派车");
 var before=f.Port.Calls.Count;await f.Service.TickAsync(default);Check(f.Port.Calls.Count==before,"暂停不增呼叫");
 f.Port.Bad=false;var held=(await f.Service.ReadAsync(default)).Current!;
 await f.Service.ControlAsync(user,"resume",new(held.RunId,"已核对两台原车原任务与点位状态",true),default);
 Check((await f.Service.ReadAsync(default)).Current!.Stage=="Running","显式现场确认续接");
 var restartOwner=new FleetTestOwnership(new(){Enabled=false});var restarted=new FleetTestService(f.Port,restartOwner,f.Path);
 Check((await restarted.ReadAsync(default)).Current!.Stage=="Hold"&&restartOwner.Active&&!restartOwner.Allows(state.Calls[0].TaskId!,"AGV-01"),"切普通模式重启仍保留暂停保护");
 var created=f.Port.Creates;await restarted.TickAsync(default);Check(f.Port.Creates==created,"重启不自动创建");
}
var stopped=Fixture();await stopped.Service.StartAsync(user,Plan("Staggered"),default);await stopped.Service.TickAsync(default);
var run=(await stopped.Service.ReadAsync(default)).Current!;
await stopped.Service.ControlAsync(user,"stop",new(run.RunId,"",false),default);stopped.Port.Milestone(DispatchTaskStepType.PickupFullBox);await stopped.Service.TickAsync(default);
Check(stopped.Port.Calls.Count==1&&stopped.Owner.Active,"停止新增保留原任务，不触发第二工位");
stopped.Port.Details[0]=stopped.Port.Details[0] with {Task=stopped.Port.Details[0].Task with {Stage=DispatchTaskStage.Completed}};
await stopped.Service.TickAsync(default);
Check((await stopped.Service.ReadAsync(default)).Current!.Stage=="Running","任务结束但PLC或资源未闭合时继续等待");
stopped.Port.Closed=true;await stopped.Service.TickAsync(default);
Check((await stopped.Service.ReadAsync(default)).Current!.Stage=="Completed"&&!stopped.Owner.Active,"确认任务及基线闭合后解除会话保护");
var closePending=Fixture();await closePending.Service.StartAsync(user,Plan("Staggered"),default);await closePending.Service.TickAsync(default);
var pendingRun=(await closePending.Service.ReadAsync(default)).Current!;
await closePending.Service.ControlAsync(user,"stop",new(pendingRun.RunId,"",false),default);
try{await closePending.Service.ControlAsync(user,"close",new(pendingRun.RunId,"现场已核对但仍不能提前结束",true),default);throw new Exception("未闭合原任务不应允许关闭");}
catch(FleetClosurePendingException){count++;}
var closeReason=(await closePending.Service.ReadAsync(default)).Current!.Message;
await closePending.Service.TickAsync(default);
Check(closeReason.Contains("still active")&&(await closePending.Service.ReadAsync(default)).Current!.Message==closeReason,"关闭失败原因不会被通用Hold提示覆盖");
var uncertain=Fixture();uncertain.Port.FailCall=true;await uncertain.Service.StartAsync(user,Plan("Simultaneous"),default);await uncertain.Service.TickAsync(default);
Check((await uncertain.Service.ReadAsync(default)).Current is {Stage:"Hold"} row&&row.Calls.All(c=>c.Released)&&uncertain.Port.Creates==0,"批次写结果不确定保留意图且不发车");
await Reject(()=>uncertain.Service.StartAsync(user,Plan("Staggered"),default),"未闭合禁止新会话");
foreach(var scenario in new[]{"Staggered","Simultaneous","Handover"})
{
 var f=Fixture();var plan=Plan(scenario) with {StationIds=["CNC-001","CNC-003","CNC-004","CNC-002"]};
 await f.Service.StartAsync(user,plan,default);await f.Service.TickAsync(default);
 Check(f.Port.Calls.Count==(scenario=="Simultaneous"?4:1),"四工位首批数量与用例一致");
 if(scenario!="Simultaneous")for(var index=0;index<3;index++)
 {
  await f.Service.TickAsync(default);Check(f.Port.Calls.Count==index+1,"四工位不能沿用首工位里程碑提前释放后续呼叫");
  var detail=f.Port.Details[index];
  f.Port.Details[index]=detail with {Steps=[new("s"+index,2,scenario=="Staggered"?DispatchTaskStepType.PickupFullBox:DispatchTaskStepType.DumpFullBox,DispatchTaskStepStatus.Completed,"AGV-01",null,null,null,0,null,DateTimeOffset.UtcNow,null)]};
  await f.Service.TickAsync(default);Check(f.Port.Calls.Count==index+2,"四工位逐个按前一工位里程碑释放");
 }
 Check(f.Port.Calls.SequenceEqual(plan.StationIds)&&f.Port.Creates==4,"四工位顺序保留且各建单一次");
 await f.Service.TickAsync(default);Check(f.Port.Creates==4,"四工位重复轮询不重复建单");
 await Reject(()=>f.Service.StartAsync(user,plan with {StationIds=["CNC-001","CNC-002"]},default),"原申请不得更换工位列表");
}
var invalid=Fixture();await Reject(()=>invalid.Service.StartAsync(user,Plan("Staggered") with {StationIds=["CNC-001","cnc-001"]},default),"拒绝重复工位");
await Reject(()=>invalid.Service.StartAsync(user,Plan("Staggered") with {StationIds=[]},default),"拒绝空工位列表");
Check(FleetTestPort.StartingPickupOccupancyConflict(
    new Dictionary<string,string>{["AGV-01"]="LM1",["AGV-03"]="LM2"},
    new Dictionary<string,string>{["CNC-001"]="LM2",["CNC-002"]="LM4"})?.Contains("LM2已被参与车辆AGV-03占用")==true,
    "拒绝把取货任务发往另一辆参与车辆当前占用的站点");
Check(FleetTestPort.StartingPickupOccupancyConflict(
    new Dictionary<string,string>{["AGV-01"]="LM7",["AGV-03"]="LM8"},
    new Dictionary<string,string>{["CNC-001"]="LM2",["CNC-002"]="LM4"}) is null,
    "两车独立待机点且不占业务取货点时可启动");
var mode=WebLocalLaunchMode.Parse(["--fleet-test-vehicles","AGV-01,AGV-02"]);
Check(mode.IsBusiness&&mode.ServiceArguments("test").Contains("--Dispatching:MaxConcurrentTasks=2")&&mode.ServiceArguments("test").Contains("--TaskCreation:AutomaticCreationEnabled=false"),"专用双车启动且无独立自动建单器");
await Reject(()=>Task.FromResult(WebLocalLaunchMode.Parse(["--fleet-test-vehicles","AGV-01,AGV-01"])),"同车不能冒充双车");
await Reject(()=>Task.FromResult(WebLocalLaunchMode.Parse(["--fleet-test-vehicles","AGV-01,AGV-02","--business-run-vehicle","AGV-01"])),"两种运行模式互斥");
Console.WriteLine($"PASS {count} 双车用例、身份、断连、停止、重启及幂等检查；无现场通信。");

sealed class Port:IFleetTestPort
{
 public int Starts,Creates;public bool Bad,FailCall,Closed;public List<string> Calls=[];public List<TaskCenterDetail> Details=[];
 public Task<(string,string,string)> ValidateAsync(FleetTestStart p,CancellationToken ct)=>Task.FromResult(("M","V","R"));
 public Task<FleetTestStatus> ObserveAsync(FleetTestRun? run,CancellationToken ct)=>Task.FromResult(new FleetTestStatus(true,["AGV-01","AGV-02"],run,[],Details.ToArray(),DateTimeOffset.UtcNow));
 public Task LabAsync(FleetTestRun r,string action,string[] stations,CancellationToken ct)
 {if(action=="start")Starts++;if(action=="call"){if(FailCall)throw new InvalidOperationException("timeout after write");Calls.AddRange(stations);}return Task.CompletedTask;}
 public Task<string?> CreateAsync(FleetTestRun r,string station,CancellationToken ct)
 {Creates++;var id="T-"+station;Details.Add(new(new(id,station,null,DispatchTaskStage.WaitingForVehicle,0,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,null),[],[],[],[],""));return Task.FromResult<string?>(id);}
 public Task AcknowledgeAsync(string s,string t,CancellationToken ct)=>Task.CompletedTask;
 public Task FeedbackAsync(FleetTestRun r,TaskCenterDetail d,CancellationToken ct)=>Task.CompletedTask;
 public Task CheckCurrentAsync(FleetTestRun r,CancellationToken ct){if(Bad)throw new InvalidOperationException("AGV-02 stale");return Task.CompletedTask;}
 public Task CheckClosedAsync(FleetTestRun r,CancellationToken ct)=>Closed?Task.CompletedTask:throw new FleetClosurePendingException("still active");
 public void Milestone(DispatchTaskStepType step)=>Details[0]=Details[0] with {Steps=[new("s",2,step,DispatchTaskStepStatus.Completed,"AGV-01",null,null,null,0,null,DateTimeOffset.UtcNow,null)]};
}
