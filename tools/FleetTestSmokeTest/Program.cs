using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;
using AgvDispatch.WebHost;
using Microsoft.Extensions.Logging;

var root=Path.Combine(Path.GetTempPath(),"fleet-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var count=0;
void Check(bool condition,string text){if(!condition)throw new Exception(text);count++;}
async Task Reject(Func<Task> operation,string text){try{await operation();}catch(InvalidOperationException){count++;return;}catch(ArgumentException){count++;return;}throw new Exception(text);}
var user=TrafficUserContext.LocalOperation("fleet-test");
FleetTestStart Plan(string scenario)=>new(Guid.NewGuid().ToString("N"),scenario,["AGV-01","AGV-02"],true){StationIds=["CNC-001","CNC-002"]};
(FleetTestService Service,Port Port,FleetTestOwnership Owner,string Path,RecordingLogger Logger) Fixture(bool enabled=true,string[]? vehicleIds=null)
{var p=new Port();var o=new FleetTestOwnership(new(){Enabled=enabled,VehicleIds=vehicleIds??["AGV-01","AGV-02"]});var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".json");var logger=new RecordingLogger();return(new(p,o,path,logger),p,o,path,logger);}
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
 var trigger=(await f.Service.ReadAsync(default)).Current!;
 Check(f.Logger.Messages.Any(m=>m.Contains(trigger.RunId)&&m.Contains("AGV-02 stale")),"暂停日志含原运行编号及触发原因");
 Check(trigger.Holds.Length==1&&trigger.Holds[0].Reason.Contains("AGV-02 stale"),"暂停原因及时间持久化");
 f.Port.Bad=false;await f.Service.TickAsync(default);
 Check((await f.Service.ReadAsync(default)).Current!.Message==trigger.Message,"状态恢复不能覆盖最初暂停原因");
 Check((await f.Service.ReadAsync(default)).Current!.UpdatedAt==trigger.UpdatedAt,"Hold轮询不伪造状态更新时间");
 var held=(await f.Service.ReadAsync(default)).Current!;
 await f.Service.ControlAsync(user,"resume",new(held.RunId,"已核对两台原车原任务与点位状态",true),default);
 Check((await f.Service.ReadAsync(default)).Current is {Stage:"Running",Holds.Length:1},"显式现场确认续接并保留暂停历史");
 var restartOwner=new FleetTestOwnership(new(){Enabled=false});var restarted=new FleetTestService(f.Port,restartOwner,f.Path);
 Check((await restarted.ReadAsync(default)).Current!.Stage=="Hold"&&restartOwner.Active&&!restartOwner.Allows(state.Calls[0].TaskId!,"AGV-01"),"切普通模式重启仍保留暂停保护");
 var created=f.Port.Creates;await restarted.TickAsync(default);Check(f.Port.Creates==created,"重启不自动创建");
}
var controls=Fixture();await controls.Service.StartAsync(user,Plan("Simultaneous"),default);
var original=(await controls.Service.ReadAsync(default)).Current!;
foreach(var action in new[]{"resume","close","unknown"})
{
 await Reject(()=>controls.Service.ControlAsync(user,action,new(original.RunId,"现场核对依据有效但操作状态不符",true),default),"非法控制须拒绝");
 Check((await controls.Service.ReadAsync(default)).Current==original,"非法控制不得暂停原运行或添加虚假核对记录");
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
var completed=(await stopped.Service.ReadAsync(default)).Current!;
await Reject(()=>stopped.Service.ControlAsync(user,"stop",new(completed.RunId,"",false),default),"已完成记录不允许再次停止");
Check((await stopped.Service.ReadAsync(default)).Current==completed,"已完成记录保持不可变");
var autoReturn=Fixture();autoReturn.Port.ReturnAlreadyComplete=false;
await autoReturn.Service.StartAsync(user,Plan("Simultaneous"),default);await autoReturn.Service.TickAsync(default);
autoReturn.Port.Closed=true;
for(var i=0;i<autoReturn.Port.Details.Count;i++)
    autoReturn.Port.Details[i]=autoReturn.Port.Details[i] with {Task=autoReturn.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await autoReturn.Service.TickAsync(default);
await autoReturn.Service.TickAsync(default);
var returned=(await autoReturn.Service.ReadAsync(default)).Current!;
Check(returned.Stage=="Completed"&&autoReturn.Port.ReturnSends==2&&
    returned.StandbyReturns.Length==2&&returned.StandbyReturns.All(r=>r.State==FleetTestStandbyReturnStates.Completed),
    "业务与PLC闭合后每车发送一次独立待机导航并核对停靠");
Check(!autoReturn.Owner.Active,"全部车辆到待机位后才解除多车会话保护");
var localizationPending=Fixture();localizationPending.Port.PlanPending=true;
await localizationPending.Service.StartAsync(user,Plan("Simultaneous"),default);await localizationPending.Service.TickAsync(default);
localizationPending.Port.Closed=true;
for(var i=0;i<localizationPending.Port.Details.Count;i++)
    localizationPending.Port.Details[i]=localizationPending.Port.Details[i] with {Task=localizationPending.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await localizationPending.Service.TickAsync(default);
Check((await localizationPending.Service.ReadAsync(default)).Current is {Stage:"Running",Holds.Length:0,StandbyReturns.Length:0}&&localizationPending.Owner.Active,
    "待机规划遇到短暂定位观察时保留Running，不误完成或进入Hold");
localizationPending.Port.PlanPending=false;await localizationPending.Service.TickAsync(default);
Check((await localizationPending.Service.ReadAsync(default)).Current is {Stage:"Completed",Holds.Length:0},
    "待机规划条件恢复后自动推进原组，不需人工续接");
var incompleteReturn=Fixture();incompleteReturn.Port.ReturnAlreadyComplete=false;incompleteReturn.Port.ReturnObserveComplete=false;
await incompleteReturn.Service.StartAsync(user,Plan("Simultaneous"),default);await incompleteReturn.Service.TickAsync(default);
incompleteReturn.Port.Closed=true;
for(var i=0;i<incompleteReturn.Port.Details.Count;i++)
    incompleteReturn.Port.Details[i]=incompleteReturn.Port.Details[i] with {Task=incompleteReturn.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await incompleteReturn.Service.TickAsync(default);
var movingReturn=(await incompleteReturn.Service.ReadAsync(default)).Current!;
await incompleteReturn.Service.ControlAsync(user,"stop",new(movingReturn.RunId,"",false),default);
try{await incompleteReturn.Service.ControlAsync(user,"close",new(movingReturn.RunId,"现场核对业务已闭合但待机车辆还在运动",true),default);throw new Exception("待机还在运动不能关闭");}
catch(FleetClosurePendingException){count++;}
Check((await incompleteReturn.Service.ReadAsync(default)).Current is {Stage:"Hold"}&&incompleteReturn.Owner.Active,
    "业务闭合不能提前关闭仍在执行的待机导航或解除车辆保护");
Check(incompleteReturn.Port.ReturnSends==1,"关闭核对只查询原待机记录不再次发送");
var returnHold=Fixture();returnHold.Port.ReturnAlreadyComplete=false;returnHold.Port.FailStandbySend=true;
await returnHold.Service.StartAsync(user,Plan("Simultaneous"),default);await returnHold.Service.TickAsync(default);
returnHold.Port.Closed=true;
for(var i=0;i<returnHold.Port.Details.Count;i++)
    returnHold.Port.Details[i]=returnHold.Port.Details[i] with {Task=returnHold.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await returnHold.Service.TickAsync(default);
var heldReturn=(await returnHold.Service.ReadAsync(default)).Current!;
Check(heldReturn.Stage=="Hold"&&heldReturn.StandbyReturns.All(r=>r.State==FleetTestStandbyReturnStates.Assigned),
    "待机发送前异常转为Hold，不退出后台且保留原分配");
returnHold.Port.FailStandbySend=false;
await returnHold.Service.ControlAsync(user,"reconcile",
    new(heldReturn.RunId,"现场已核对原待机导航及车辆实际停靠",true),default);
var reconciled=(await returnHold.Service.ReadAsync(default)).Current!;
Check(reconciled.Stage=="Completed"&&reconciled.StandbyReturns.All(r=>r.State==FleetTestStandbyReturnStates.Completed),
    "核对待机路权按原记录闭合且不重发业务任务");
var interruptedReturn=Fixture();interruptedReturn.Port.ReturnAlreadyComplete=false;interruptedReturn.Port.FailStandbyAfterIntent=true;
await interruptedReturn.Service.StartAsync(user,Plan("Simultaneous"),default);await interruptedReturn.Service.TickAsync(default);
interruptedReturn.Port.Closed=true;
for(var i=0;i<interruptedReturn.Port.Details.Count;i++)
    interruptedReturn.Port.Details[i]=interruptedReturn.Port.Details[i] with {Task=interruptedReturn.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await interruptedReturn.Service.TickAsync(default);
var uncertainReturn=(await interruptedReturn.Service.ReadAsync(default)).Current!;
Check(uncertainReturn.Stage=="Hold"&&uncertainReturn.StandbyReturns[0] is {State:FleetTestStandbyReturnStates.Uncertain,VendorTaskId:not null}&&
    uncertainReturn.StandbyReturns[0].Route.Count==2,"发送中断后持久化Uncertain及原厂家编号路线，不复位为Assigned");
var interruptedRestart=new FleetTestService(interruptedReturn.Port,new FleetTestOwnership(new(){Enabled=true}),interruptedReturn.Path);
await Reject(()=>interruptedRestart.ControlAsync(user,"resume",new(uncertainReturn.RunId,"现场核对车端原任务发送结果仍需只读核验",true),default),"重启后不能续接重发结果不确定的原导航");
Check(interruptedReturn.Port.ReturnSends==1,"发送中断及重启核对不重复发送");
await interruptedRestart.ControlAsync(user,"reconcile",new(uncertainReturn.RunId,"已核对原任务完成并实际停靠在各自待机位置",true),default);
Check((await interruptedRestart.ReadAsync(default)).Current!.Stage=="Completed"&&interruptedReturn.Port.ReturnSends==1,
    "结果不确定的待机命令按原记录核对闭合且不重发");
var failedResult=Fixture();failedResult.Port.ReturnAlreadyComplete=false;failedResult.Port.FailStandbyGenericAfterIntent=true;
await failedResult.Service.StartAsync(user,Plan("Simultaneous"),default);await failedResult.Service.TickAsync(default);
failedResult.Port.Closed=true;
for(var i=0;i<failedResult.Port.Details.Count;i++)
    failedResult.Port.Details[i]=failedResult.Port.Details[i] with {Task=failedResult.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await failedResult.Service.TickAsync(default);
var failedResultRun=(await failedResult.Service.ReadAsync(default)).Current!;
Check(failedResultRun.Stage=="Hold"&&failedResultRun.StandbyReturns[0] is {State:FleetTestStandbyReturnStates.Uncertain,VendorTaskId:not null},
    "发送意图落盘后普通异常不能将原导航复位为Assigned");
await failedResult.Service.TickAsync(default);
Check(failedResult.Port.ReturnSends==1,"结果处理异常后重复轮询仍不重发待机命令");
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
var persisted=new FleetTestService(uncertain.Port,new FleetTestOwnership(new(){Enabled=true}),uncertain.Path);
var reloaded=(await persisted.ReadAsync(default)).Current!;
Check(reloaded.Holds.Length==2&&reloaded.Holds[0].Reason.Contains("timeout after write"),"重启保留原暂停历史并追加重启原因");
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
// Arbitrary identifiers and fleet sizes exercise the same persistent state machine.
foreach(var size in new[]{3,5,7})
{
 var ids=Enumerable.Range(1,size+1).Select(i=>"MOVER-"+i).ToArray();
 var f=Fixture(vehicleIds:ids);
 var plan=new FleetTestStart(Guid.NewGuid().ToString("N"),"Simultaneous",ids[..size],true)
 {StationIds=Enumerable.Range(1,size*3).Select(i=>"CELL-"+i).ToArray()};
 await f.Service.StartAsync(user,plan,default);await f.Service.TickAsync(default);await f.Service.TickAsync(default);
 var state=(await f.Service.ReadAsync(default)).Current!;
 Check(f.Port.Creates==size*3&&state.Calls.All(c=>c.TaskId!=null),$"{size}车非固定编号工位各建一单");
 Check(plan.VehicleIds.All(id=>f.Owner.Allows(state.Calls[0].TaskId!,id))&&!f.Owner.Allows(state.Calls[0].TaskId!,ids[^1]),"配置车辆清单其余车辆不能领取本轮任务");
 f.Port.Closed=true;
 for(var i=0;i<f.Port.Details.Count;i++)f.Port.Details[i]=f.Port.Details[i] with {Task=f.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
 await f.Service.TickAsync(default);
 Check((await f.Service.ReadAsync(default)).Current!.Stage=="Completed",$"{size}车任务及握手闭合后完成");
 await f.Service.StartAsync(user,plan,default);
 Check(f.Port.Creates==size*3&&f.Port.Starts==1,"多车完成记录幂等不重放");
}
var configuredVehicles=new[]{"CART-A","CART-B","CART-C","CART-D"};
var selected=new FleetTestStart(Guid.NewGuid().ToString("N"),"Simultaneous",["CART-A","CART-C"],true){StationIds=["LINE-X","LINE-Z"]};
var pools=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase){["LINE-X"]=["CART-A","CART-B"],["LINE-Z"]=["CART-C","CART-D"]};
Check(FleetTestPort.SelectionError(selected,configuredVehicles,pools)==null,"多个车辆池允许本轮选取各池子集");
Check(FleetTestPort.SelectionError(selected with {VehicleIds=["CART-A","OUTSIDE"]},configuredVehicles,pools)?.Contains("超出")==true,"拒绝启动配置外车辆");
Check(FleetTestPort.SelectionError(selected with {VehicleIds=["CART-A","CART-B"]},configuredVehicles,pools)?.Contains("LINE-Z")==true,"列出无可服务车辆的具体工位");
Check(FleetTestPort.SelectionError(selected with {VehicleIds=["CART-A","CART-C","CART-D"]},configuredVehicles,pools)==null,"一站允许多辆候选车");
Check(FleetTestPort.SelectionError(selected with {VehicleIds=["CART-A","CART-C","CART-B"],StationIds=["LINE-Z"]},configuredVehicles,pools)?.Contains("CART-A")==true,"拒绝不能服务任一所选工位的参与车辆");
foreach(var max in new[]{1,2,4})Check(new FleetTestOptions{Enabled=true,VehicleIds=configuredVehicles}.ConfigurationError(max)==null,"并发上限按配置允许，不固定为车数或2");
Check(new FleetTestOptions{Enabled=true,VehicleIds=configuredVehicles}.ConfigurationError(5)!=null,"拒绝超过配置车辆数的并发");
var genericMode=WebLocalLaunchMode.Parse(["--fleet-test-vehicles",string.Join(',',configuredVehicles),"--fleet-test-max-concurrent","3"]);
Check(genericMode.FleetVehicles.Length==4&&genericMode.FleetMaxConcurrentTasks==3&&genericMode.ServiceArguments("test").Contains("--FleetTest:VehicleIds:3=CART-D")&&genericMode.ServiceArguments("test").Contains("--Dispatching:MaxConcurrentTasks=3"),"启动参数包含全部车辆并支持独立并发配置");
Check(WebLocalLaunchMode.Parse(["--fleet-test-vehicles",string.Join(',',configuredVehicles)]).FleetMaxConcurrentTasks==4,"未指定并发时默认配置车辆数");
foreach(var value in new[]{"0","5","text"})await Reject(()=>Task.FromResult(WebLocalLaunchMode.Parse(["--fleet-test-vehicles",string.Join(',',configuredVehicles),"--fleet-test-max-concurrent",value])),"拒绝无效并发参数");
await Reject(()=>Task.FromResult(WebLocalLaunchMode.Parse(["--fleet-test-max-concurrent","2"])),"并发参数不能脱离多车启动模式");
Check(new FleetTestStart("","",[],false).StationIds.Length==0,"协议不再自动注入固定工位编号");
Check(FleetTestPort.SelectionError(selected with {VehicleIds=["cart-a","cart-c"],StationIds=["line-x","line-z"]},configuredVehicles,pools)==null,"车辆池和参与车辆比较忽略编号大小写");
var retirement=Fixture();var rejectedPlan=Plan("Simultaneous");retirement.Port.FailValidate=true;
await Reject(()=>retirement.Service.StartAsync(user,rejectedPlan,default),"启动校验失败不得创建原运行");
Check((await retirement.Service.ReadAsync(default)).Current==null&&retirement.Port.Starts==0,"拒绝申请无运行、PLC启动或呼叫");
var retireRequest=new FleetTestStartRetirement(rejectedPlan.RequestId,"已查询原申请并核对现场无设备动作",true);
var receipt=await retirement.Service.RetireStartAsync(user,retireRequest,default);
Check(receipt.Retired&&receipt.RequestId==rejectedPlan.RequestId&&File.Exists(retirement.Path+".retired"),"未受理原申请保存明确注销回执");
var repeatedReceipt=await retirement.Service.RetireStartAsync(user,retireRequest,default);
Check(repeatedReceipt.RetiredAt==receipt.RetiredAt,"重复注销幂等保留原回执时间");
retirement.Port.FailValidate=false;
await Reject(()=>retirement.Service.StartAsync(user,rejectedPlan,default),"注销后迟到启动必须拒绝");
await Reject(()=>retirement.Service.StartAsync(user,rejectedPlan with {RequestId=rejectedPlan.RequestId.ToUpperInvariant()},default),"编号大小写不能绕过注销墓碑");
var retirementRestart=new FleetTestService(retirement.Port,new FleetTestOwnership(new(){Enabled=true}),retirement.Path);
await Reject(()=>retirementRestart.StartAsync(user,rejectedPlan,default),"重启后注销请求仍拒绝");
Check(retirement.Port.Starts==0&&retirement.Port.Creates==0&&!retirement.Owner.Active,"注销不会发送动作或产生原占用");
var invalidRetirement=Fixture();
foreach(var badRetirement in new[]{retireRequest with {SiteConfirmed=false},retireRequest with {Reason="短"},retireRequest with {RequestId="invalid"},retireRequest with {Reason=new string('x',1001)}})
 await Reject(()=>invalidRetirement.Service.RetireStartAsync(user,badRetirement,default),"未现场确认或依据无效不能注销");
try{await invalidRetirement.Service.RetireStartAsync(new("viewer","viewer",TrafficPermission.View),retireRequest,default);throw new Exception("只读用户不能注销");}
catch(UnauthorizedAccessException){count++;}
Check(!File.Exists(invalidRetirement.Path+".retired"),"不合法注销不写入持久化文件");
var accepted=Fixture();var acceptedPlan=Plan("Simultaneous");await accepted.Service.StartAsync(user,acceptedPlan,default);
var acceptedRun=(await accepted.Service.ReadAsync(default)).Current!;
await Reject(()=>accepted.Service.RetireStartAsync(user,new(acceptedPlan.RequestId,"已接受申请必须查询原运行而不能注销",true),default),"已接受当前运行不得注销");
Check((await accepted.Service.ReadAsync(default)).Current==acceptedRun&&accepted.Owner.Active&&!File.Exists(accepted.Path+".retired"),"退休冲突不改当前运行或占用");
await accepted.Service.TickAsync(default);accepted.Port.Closed=true;
for(var i=0;i<accepted.Port.Details.Count;i++)accepted.Port.Details[i]=accepted.Port.Details[i] with {Task=accepted.Port.Details[i].Task with {Stage=DispatchTaskStage.Completed}};
await accepted.Service.TickAsync(default);await accepted.Service.StartAsync(user,Plan("Staggered"),default);
await Reject(()=>accepted.Service.RetireStartAsync(user,new(acceptedPlan.RequestId,"原接受申请已经归档仍不得注销重放",true),default),"历史原运行不得注销");
var racing=Fixture();var racingPlan=Plan("Simultaneous");
racing.Port.ValidationStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);racing.Port.ValidationGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
var startInFlight=racing.Service.StartAsync(user,racingPlan,default);
await racing.Port.ValidationStarted.Task;
var racingRetire=racing.Service.RetireStartAsync(user,new(racingPlan.RequestId,"核对时原启动仍在执行应等待明确结果",true),default);
Check(!racingRetire.IsCompleted,"注销等待原启动排他锁，不能边校验边清除");
racing.Port.ValidationGate.SetResult();await startInFlight;
await Reject(()=>racingRetire,"在途启动先受理时注销必须拒绝");
Check(racing.Port.Starts==1&&!File.Exists(racing.Path+".retired"),"并发控制不产生接受与注销的矛盾回执");
var receiptJson=System.Text.Json.JsonSerializer.Serialize(receipt,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
Check(receiptJson.Contains("\"requestId\"")&&receiptJson.Contains("\"retired\":true"),"Web回执字段符合前端原申请清除契约");
var autoDispatch=new DispatchOrchestratorOptions{MaxConcurrentTasks=3,DumperWaitingSite="LM35",DumperWaitingSites=["LM35"],DumperDumpingSite="LM39"};
var autoParameters=new TaskParameterRuntime(autoDispatch,new DispatchTaskCreationOptions{DumperStationId="DUMPER-001"});
var autoProfile=autoParameters.Capture() with
{
    DumperStationId="DUMPER-001",WaitingSite="LM35",DumpingSite="LM39",WaitingSites=["LM35"],
    Pickups=[new TaskPickupParameter("CNC-001","LM1")],
    Actions=
    [
        new(DispatchTaskStepType.GoToPickup,"JackLoad",null,null,null,null),
        new(DispatchTaskStepType.DeliverFullBoxToDumper,"Wait",null,null,null,null),
        new(DispatchTaskStepType.EnterDumpingPosition,"Wait",null,null,null,null),
        new(DispatchTaskStepType.ReturnEmptyBox,"JackUnload",null,null,null,null)
    ]
};
autoParameters.Apply(new(1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"MAP","V1",autoProfile,[]));
var autoMap=new MapDefinition("waiting-bays","","RobotWorld","m",
[
    new("BUS-LM35",0,0,MapNodeType.WaitingPoint,true,["DUMPER-WAIT-LM35"],"LM35"),
    new("BUS-LM36",1,0,MapNodeType.WaitingPoint,true,["DUMPER-WAIT-LM36"],"LM36"),
    new("OTHER-WAIT",2,0,MapNodeType.WaitingPoint,true,["GENERAL-WAIT"],"LM37"),
    new("DUP-A",3,0,MapNodeType.WaitingPoint,true,["DUMPER-WAIT-DUP"],"LM38A"),
    new("DUP-B",4,0,MapNodeType.WaitingPoint,true,["DUMPER-WAIT-DUP"],"LM38B"),
    new("BUS-LM39",5,0,MapNodeType.Station,true,["DUMPER-AREA-001"],"LM39")
],
[],
[
    new("DUMPER-WAIT-LM35",TrafficResourceType.Station,1),
    new("DUMPER-WAIT-LM36",TrafficResourceType.Station,1),
    new("GENERAL-WAIT",TrafficResourceType.Station,1),
    new("DUMPER-WAIT-DUP",TrafficResourceType.Station,1),
    new("DUMPER-AREA-001",TrafficResourceType.Station,1)
]);
Check(autoParameters.ExpandWaitingSitesFromMap(autoMap),"多车模式从地图补齐倒料候车位");
Check(autoDispatch.GetDumperWaitingSites().SequenceEqual(["LM35","LM36"]),"仅加入唯一映射且命名明确的 DUMPER-WAIT 容量1站点");
Check(autoParameters.Capture().WaitingSites.SequenceEqual(["LM35","LM36"]),"动态补齐结果同步到运行时任务参数");
Check(!autoParameters.ExpandWaitingSitesFromMap(autoMap),"候车位自动发现幂等");
var blockedStart=DateTimeOffset.UtcNow;
var blockedTracker=new BlockedObservationTracker(TimeSpan.FromSeconds(3));
Check(!blockedTracker.Observe("AGV-X",true,false,blockedStart),"瞬时阻挡不立即暂停");
Check(!blockedTracker.Observe("AGV-X",true,false,blockedStart.AddSeconds(2)),"阻挡未满阈值继续观察");
Check(blockedTracker.Observe("AGV-X",true,false,blockedStart.AddSeconds(3)),"静止持续阻挡达到阈值才暂停");
Check(!blockedTracker.Observe("AGV-X",true,true,blockedStart.AddSeconds(4)),"避让中的移动车辆不暂停");
Check(!blockedTracker.Observe("AGV-X",true,false,blockedStart.AddSeconds(4.2)),"移动后阻挡计时重新开始");
Check(!blockedTracker.Observe("AGV-X",false,false,blockedStart.AddSeconds(4.4)),"阻挡清除后立即复位");
Check(FleetTestPort.IsLocalTrafficObstacleAlarm("52200","blocked by another vehicle")&&
    !FleetTestPort.IsLocalTrafficObstacleAlarm("E-STOP","emergency stop"),"车辆局部阻挡仅由车端处理，其他故障仍暂停全组");
foreach(var code in new[]{"E-STOP","52100","152200","UNKNOWN",null})
    Check(!FleetTestPort.IsLocalTrafficObstacleAlarm(code,"motor blocked"),"非52200故障不因描述包含blocked而被忽略");
await StandbySendChecks.RunAsync(Check);
await StandbyChainChecks.RunAsync(Check);
await LocalizationChecks.RunAsync(Check);
Console.WriteLine($"PASS {count} 多车多工位、车辆池范围、身份、断连、停止、重启及幂等检查；无现场通信。");

sealed class Port:IFleetTestPort
{
 public int Starts,Creates,ReturnSends;public bool Bad,FailCall,Closed,FailValidate;
 public bool ReturnAlreadyComplete=true,ReturnObserveComplete=true,FailStandbySend,FailStandbyAfterIntent,FailStandbyGenericAfterIntent,PlanPending;
 public List<string> Calls=[];public List<TaskCenterDetail> Details=[];
 public TaskCompletionSource? ValidationStarted,ValidationGate;
 public async Task<(string,string,string)> ValidateAsync(FleetTestStart p,CancellationToken ct)
 {ValidationStarted?.TrySetResult();if(ValidationGate!=null)await ValidationGate.Task.WaitAsync(ct);if(FailValidate)throw new InvalidOperationException("configuration rejected before acceptance");return("M","V","R");}
 public Task<FleetTestStatus> ObserveAsync(FleetTestRun? run,CancellationToken ct)=>Task.FromResult(new FleetTestStatus(true,["AGV-01","AGV-02"],run,[],Details.ToArray(),DateTimeOffset.UtcNow));
 public Task LabAsync(FleetTestRun r,string action,string[] stations,CancellationToken ct)
 {if(action=="start")Starts++;if(action=="call"){if(FailCall)throw new InvalidOperationException("timeout after write");Calls.AddRange(stations);}return Task.CompletedTask;}
 public Task<string?> CreateAsync(FleetTestRun r,string station,CancellationToken ct)
 {Creates++;var id="T-"+station;Details.Add(new(new(id,station,null,DispatchTaskStage.WaitingForVehicle,0,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,null),[],[],[],[],""));return Task.FromResult<string?>(id);}
 public Task AcknowledgeAsync(string s,string t,CancellationToken ct)=>Task.CompletedTask;
 public Task FeedbackAsync(FleetTestRun r,TaskCenterDetail d,CancellationToken ct)=>Task.CompletedTask;
 public Task CheckCurrentAsync(FleetTestRun r,CancellationToken ct){if(Bad)throw new InvalidOperationException("AGV-02 stale");return Task.CompletedTask;}
 public Task CheckClosedAsync(FleetTestRun r,CancellationToken ct)=>Closed?Task.CompletedTask:throw new FleetClosurePendingException("still active");
 public Task<FleetTestStandbyReturn[]> PlanStandbyReturnsAsync(FleetTestRun r,CancellationToken ct)=>
  PlanPending?Task.FromException<FleetTestStandbyReturn[]>(new FleetClosurePendingException("localization observation pending")):
  Task.FromResult(r.Plan.VehicleIds.Select((id,i)=>new FleetTestStandbyReturn(id,"B-"+i,"LM"+(29+i),
   ReturnAlreadyComplete?FleetTestStandbyReturnStates.Completed:FleetTestStandbyReturnStates.Assigned,
   ReturnAlreadyComplete?"already home":"assigned",null,DateTimeOffset.UtcNow)).ToArray());
 public Task<FleetTestStandbyReturn?> TrySendStandbyReturnAsync(FleetTestRun r,FleetTestStandbyReturn[] returns,
  FleetTestStandbyReturn assignment,Action<FleetTestStandbyReturn> persistSendIntent,CancellationToken ct)
 {if(FailStandbySend)throw new InvalidOperationException("standby route unavailable");ReturnSends++;
  if(FailStandbyAfterIntent)
  {persistSendIntent(assignment with {VendorTaskId="V-"+assignment.VehicleId,Route=["PICKUP",assignment.Site]});throw new FleetStandbySendUncertainException("cancelled after send",new OperationCanceledException());}
  if(FailStandbyGenericAfterIntent)
  {persistSendIntent(assignment with {VendorTaskId="V-"+assignment.VehicleId,Route=["PICKUP",assignment.Site]});throw new IOException("result persistence failed after send");}
  return Task.FromResult<FleetTestStandbyReturn?>(assignment with
  {State=FleetTestStandbyReturnStates.Returning,Message="returning",VendorTaskId="V-"+assignment.VehicleId,
   UpdatedAt=DateTimeOffset.UtcNow});}
 public Task<FleetTestStandbyReturn> ObserveStandbyReturnAsync(FleetTestRun r,FleetTestStandbyReturn assignment,
  CancellationToken ct)=>Task.FromResult(ReturnObserveComplete?assignment with
  {State=FleetTestStandbyReturnStates.Completed,Message="arrived",UpdatedAt=DateTimeOffset.UtcNow}:assignment);
 public Task ReleaseStandbyReturnAsync(FleetTestStandbyReturn assignment,CancellationToken ct)=>Task.CompletedTask;
 public Task<FleetTestStandbyReturn[]> ReconcileStandbyReturnsAsync(FleetTestRun r,CancellationToken ct)=>
  Task.FromResult(r.StandbyReturns.Select(a=>a with
   {State=ReturnObserveComplete?FleetTestStandbyReturnStates.Completed:a.State,Message="reconciled",UpdatedAt=DateTimeOffset.UtcNow}).ToArray());
 public void Milestone(DispatchTaskStepType step)=>Details[0]=Details[0] with {Steps=[new("s",2,step,DispatchTaskStepStatus.Completed,"AGV-01",null,null,null,0,null,DateTimeOffset.UtcNow,null)]};
}

sealed class RecordingLogger:ILogger<FleetTestService>
{
 public List<string> Messages=[];
 public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
 public bool IsEnabled(LogLevel level)=>true;
 public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> format)=>Messages.Add(format(state,exception));
}
