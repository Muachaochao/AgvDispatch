using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;

var checks=0;
void Check(bool condition,string message) { if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message); }
var options=new DispatchOrchestratorOptions {MaxConcurrentTasks=2,DumperWaitingSite="WAIT",DumperDumpingSite="DUMP"};
var nodes=new MapNode[]{new("P1",0,0,MapNodeType.Station,true,[],"LM-P1"),new("P2",0,3,MapNodeType.Station,true,[],"LM-P2"),
    new("WAIT",5,0,MapNodeType.WaitingPoint,true,["BAY"],"LM-WAIT"),new("DUMP",10,0,MapNodeType.Station,true,["DUMPER"],"LM-DUMP")};
var resources=new TrafficResourceDefinition[]{new("BAY",TrafficResourceType.Station),new("DUMPER",TrafficResourceType.Station)};
var map=new MapDefinition("Synthetic","test","local","m",nodes,
    [new("E1","P1","WAIT",5,1,true,true,[]),new("E2","P2","WAIT",Math.Sqrt(34),1,true,true,[]),new("E3","WAIT","DUMP",5,1,false,true,["DUMPER"])],resources);
Check(DumperWaitingBaySafety.GetError(map,options,true)==null,"单等待节点绑定独立容量1资源允许多车配置");
Check(DumperWaitingBaySafety.GetError(map,options,false)!=null,"多车缺少持续保持路权契约时拒绝开始新任务");
Check(DumperWaitingBaySafety.GetError(map with{Nodes=nodes.Select(n=>n.Id=="WAIT"?n with{ResourceIds=[]}:n).ToArray()},options,true)!=null,"未绑定等待资源不能以地图可达代替容量保护");
Check(DumperWaitingBaySafety.GetError(map with{Resources=resources.Select(r=>r.Id=="BAY"?r with{Capacity=2}:r).ToArray()},options,true)!=null,"单等待点容量2不能当作两车空间");
Check(DumperWaitingBaySafety.GetError(map with{Nodes=nodes.Select(n=>n.Id=="WAIT"?n with{ResourceIds=["DUMPER"]}:n).ToArray()},options,true)!=null,"不得借倒料资源冒充独立等待位资源");
options.DumperWaitingSite="LM-WAIT";options.DumperDumpingSite="LM-DUMP";
Check(DumperWaitingBaySafety.GetError(map,options,true)==null,"厂家站点编号可唯一映射，不与业务节点编号混写");
options.DumperWaitingSite="WAIT";options.DumperDumpingSite="WAIT";
Check(DumperWaitingBaySafety.GetError(map,options,true)!=null,"等待与倒料不能同点");
options.DumperDumpingSite="DUMP";
Check(map.ToGraph().FindShortestRoute("P1","WAIT")!.ResourceIds.Contains("BAY"),"进等待位的任意路径必须带目标节点BAY资源");
Check(map.ToGraph().FindShortestRoute("P2","WAIT",new HashSet<string>{"BAY"})==null,"等待容量被占时后车没有绕开目标资源的伪替代路线");
var narrow=new MapDefinition("Opposing","test","local","m",[new("A",0,0,MapNodeType.Waypoint,true,[]),new("B",3,0,MapNodeType.Waypoint,true,[]),new("C",0,3,MapNodeType.Waypoint,true,[])],
    [new("N","A","B",3,1,true,true,["LANE"]),new("ALT1","B","C",Math.Sqrt(18),1,true,false,[]),new("ALT2","C","A",3,1,true,false,[])],[new("LANE",TrafficResourceType.SingleLane)]);
Check(narrow.ToGraph().FindShortestRoute("B","A",new HashSet<string>{"LANE"})==null,"对向单通道占用且无替代路径时不可发车");
var alternative=narrow with{Edges=narrow.Edges.Select(e=>e.Id.StartsWith("ALT")?e with{Enabled=true}:e).ToArray()};
Check(alternative.ToGraph().FindShortestRoute("B","A",new HashSet<string>{"LANE"})!.EdgeIds.SequenceEqual(["ALT1","ALT2"]),"有合法替代路径时避开占用窄道，使用真实相邻边");
var root=Path.Combine(Path.GetTempPath(),"agv-waiting-bay-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"private.db")});
var catalog=new TrafficResourceCatalog(resources);await new DatabaseInitializer(db,catalog).InitializeAsync(default);
foreach(var id in new[]{"OWNER","FOLLOWER"}) {
    await db.Insertable(new DispatchTaskEntity{TaskId=id,StationId=id,Stage=10,Priority=5,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
    await db.Insertable(new DispatchTaskStepEntity{TaskId=id,StepId=id,Status=10,Sequence=3,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
}
var manager=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(1)));
await manager.RecordRouteAsync(new("OWNER","OWNER","CAR-1","P1","WAIT",["E1"],["BAY"],false,"candidate"),default);
await manager.RecordRouteAsync(new("FOLLOWER","FOLLOWER","CAR-2","P2","WAIT",["E2"],["BAY"],false,"candidate"),default);
Check((await manager.TryAcquireAsync("OWNER","OWNER","CAR-1",["BAY"],default)).Acquired,"首车实际持久化取得候车位");
Check(!(await manager.TryAcquireAsync("FOLLOWER","FOLLOWER","CAR-2",["BAY"],default)).Acquired,"后车取得失败且返回占用方");
await db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l=>l.ExpiresAt==DateTime.Now.AddDays(-1)).Where(l=>l.TaskId=="OWNER").ExecuteCommandAsync();
await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.CancellationReview).Where(t=>t.TaskId=="OWNER").ExecuteCommandAsync();
var restarted=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(1)));
Check((await restarted.GetUnavailableResourceIdsAsync("FOLLOWER",default)).Contains("BAY")&&!(await restarted.TryAcquireAsync("FOLLOWER","FOLLOWER","CAR-2",["BAY"],default)).Acquired,
    "取消核对、心跳过期及管理器重建不自动腾出等待位");
var control=new TrafficResourceControlService(db,catalog);
var page=await control.GetWaitingAsync(new(BlockingResourceId:"BAY"),default);
Check(page.TotalWaiting==1&&page.Items.Single().Blockers.Single().TaskId=="OWNER","候车冲突进入正式等待队列及阻塞资源筛选");
var equipment=new TrafficRouteObservation("FOLLOWER","FOLLOWER","CAR-2","WAIT","DUMP",[],["DUMPER"],true,"设备由原任务占用；许可尚未交接"){WaitingCategory="Equipment"};
await manager.RecordRouteAsync(equipment,default);
var start=db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="FOLLOWER").WaitingSinceUtc;
await manager.RecordRouteAsync(equipment,default);
page=await control.GetWaitingAsync(new(),default);
Check(page.Items.Single().WaitingCategory=="Equipment"&&page.Items.Single().Reason==equipment.Reason&&db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="FOLLOWER").WaitingSinceUtc==start,
    "设备等待无道路租约时也保留真实原因及持续起点，不伪称阻塞已解除");
await manager.RecordRouteAsync(equipment with{Waiting=false,Reason="设备归属已取得，PLC尚需核验"},default);
Check((await control.GetWaitingAsync(new(),default)).TotalWaiting==0&&db.Queryable<TrafficResourceLeaseEntity>().Count()==1,
    "设备归属就绪只结束等待展示，不授予或释放任何道路资源");
await db.Ado.ExecuteCommandAsync("ALTER TABLE traffic_request DROP COLUMN WaitingCategory");
await new DatabaseInitializer(db,catalog).InitializeAsync(default);
Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==1 && db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="FOLLOWER").Reason=="设备归属已取得，PLC尚需核验",
    "旧等待表添加可空类别列时保留原请求及取消占用，不重建业务数据");
catalog.Replace(catalog.All.Append(new("RACE",TrafficResourceType.Station)));
foreach(var id in new[]{"RACER1","RACER2"})
    await db.Insertable(new DispatchTaskEntity{TaskId=id,StationId=id,Stage=10,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var arrivals=0;
var racers=await Task.WhenAll(new[]{"RACER1","RACER2"}.Select(id=>Task.Run(async()=> {
    using var connection=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"private.db")});
    var rival=new TrafficResourceManager(connection,catalog,new(TimeSpan.FromSeconds(1)));
    if(Interlocked.Increment(ref arrivals)==2)ready.SetResult();await ready.Task;
    try{return (Id:id,Acquired:(await rival.TryAcquireAsync(id,id,id,["RACE"],default)).Acquired);}
    catch(InvalidOperationException e)when(e.InnerException?.Message.Contains("locked",StringComparison.OrdinalIgnoreCase)==true)
    {return (Id:id,Acquired:false);}
})));
Check(racers.Count(r=>r.Acquired)==1 && db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.ResourceId=="RACE").ToList().Select(l=>l.TaskId).Distinct().Count()==1,
    "两个独立SQLite连接同时争用容量1，仅一方成功，不超卖候车位");
var loser=racers.Single(r=>!r.Acquired).Id;
Check(!(await manager.TryAcquireAsync(loser,loser,loser,["RACE"],default)).Acquired,"并发失败方下轮申请仍识别原占用，不以瞬时锁冲突放行");
var multiOptions = new DispatchOrchestratorOptions { MaxConcurrentTasks=3,DumperWaitingSite="WAIT",DumperWaitingSites=["WAIT","WAIT2"],DumperDumpingSite="DUMP" };
var multiMap = map with { Nodes=map.Nodes.Append(new("WAIT2",5,3,MapNodeType.WaitingPoint,true,["BAY2"],"WAIT2")).ToArray(),
    Resources=map.Resources.Append(new("BAY2",TrafficResourceType.Station)).ToArray(),
    Edges=map.Edges.Concat([new("P1W2","P1","WAIT2",6,1,false,true,[]),new("P2W2","P2","WAIT2",5,1,false,true,[]),
        new("W2D","WAIT2","DUMP",6,1,false,true,[])]).ToArray() };
Check(DumperWaitingBaySafety.GetError(multiMap,multiOptions,true)==null,"两个独立容量1候车位均受保护");
Check(DumperWaitingBaySafety.GetError(multiMap with {Nodes=multiMap.Nodes.Select(n=>n.Id=="WAIT2"?n with{ResourceIds=["BAY"]}:n).ToArray()},multiOptions,true)!=null,
    "两个候车点不能共用一个资源冒充两个独立泊位");
catalog.Replace(multiMap.Resources);
var runtime=new ActiveMapRuntimeSnapshot("MULTI","V1",multiMap,DateTimeOffset.Now);
var planner=new BayPlanner(multiMap);
async Task<DispatchTaskStepWorkItem> MultiStep(string id,string vehicle)
{
    await db.Insertable(new DispatchTaskEntity{TaskId=id,StationId=id,VehicleId=vehicle,Stage=(int)DispatchTaskStage.PickingUp,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
    await db.Insertable(new DispatchTaskStepEntity{TaskId=id,StepId=id,StepType=(int)DispatchTaskStepType.DeliverFullBoxToDumper,Status=(int)DispatchTaskStepStatus.Ready,Sequence=3,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
    return new(id,id,vehicle,DispatchTaskStage.PickingUp,id,3,DispatchTaskStepType.DeliverFullBoxToDumper,DispatchTaskStepStatus.Ready,null,null,DateTimeOffset.Now);
}
// Separate from the earlier single-bay cancellation case: release only its fixture task.
await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.Completed).Where(t=>t.TaskId=="OWNER").ExecuteCommandAsync();
await manager.GetUnavailableResourceIdsAsync("MULTI-A",default);
var ma=await MultiStep("MULTI-A","CAR-A");var mb=await MultiStep("MULTI-B","CAR-B");var mc=await MultiStep("MULTI-C","CAR-C");
var a=await TaskWaitingBayRouter.AcquireAsync(ma,"P1",multiOptions,runtime,planner,manager,manager,default);
var b=await TaskWaitingBayRouter.AcquireAsync(mb,"P2",multiOptions,runtime,planner,manager,manager,default);
Check(a is not null&&b is not null&&a.Value.Site!=b.Value.Site,"前两台车选择不同空候车位并在同事务持久占用");
Check(await TaskWaitingBayRouter.AcquireAsync(mc,"P1",multiOptions,runtime,planner,manager,manager,default) is null&&await manager.GetAsync(mc.TaskId,default) is null,
    "第三台无空位时保持原工位，不生成伪候车绑定");
var savedA=await manager.GetAsync(ma.TaskId,default);
var managerAfterRestart=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(1)));
Check(await managerAfterRestart.GetAsync(ma.TaskId,default)==savedA,"重建服务后保留原任务、车辆、地图及候车位");
await manager.ReleaseStepAsync(ma.StepId,default);
var retried=await TaskWaitingBayRouter.AcquireAsync(ma,"P2",multiOptions,runtime,planner,managerAfterRestart,managerAfterRestart,default);
Check(retried?.Site==savedA!.WaitingSite,"命令准备回滚后再次规划仍只使用原绑定候车位");
var refused=false;
try { await TaskWaitingBayRouter.AcquireAsync(ma with{VehicleId="OTHER"},"P1",multiOptions,runtime,planner,managerAfterRestart,managerAfterRestart,default); }
catch(InvalidOperationException){refused=true;}
Check(refused,"重试换车被拒绝，不能继承另一车的候车位");
refused=false;
try { await managerAfterRestart.TryAcquireAsync(savedA! with{WaitingSite="WAIT2",ResourceId="BAY2"},["BAY2"],default); }
catch(InvalidOperationException){refused=true;}
Check(refused&&await managerAfterRestart.GetAsync(ma.TaskId,default)==savedA,"禁止原任务改绑候车点，事务失败不改变原绑定");
Check((await managerAfterRestart.TryAcquireAsync(ma.StepId,ma.TaskId,ma.VehicleId!,["DUMPER"],default)).Acquired,
    "离位释放夹具同时持有后续倒料资源");
var departed=await managerAfterRestart.ReleaseDepartedWaitingBaysAsync(ma.TaskId,new HashSet<string>(["BAY","BAY2"]),
    "车辆已到达倒料点，只释放候车位",default);
Check(departed==1&&
    db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId==ma.TaskId&&(l.ResourceId=="BAY"||l.ResourceId=="BAY2")).Count()==0&&
    db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId==ma.TaskId&&l.ResourceId=="DUMPER").Count()==1,
    "候车位离位后仅释放独立候车资源，后续倒料资源继续保留");
Check(await managerAfterRestart.GetAsync(ma.TaskId,default)==savedA,"提前释放候车租约不删除原任务候车位绑定");
catalog.Replace(multiMap.Resources.Append(new("ATOMIC-BAY",TrafficResourceType.Station)));
var atomicA=await MultiStep("ATOMIC-A","VEHICLE-A");var atomicB=await MultiStep("ATOMIC-B","VEHICLE-B");
foreach(var step in new[]{atomicA,atomicB})
    await manager.RecordRouteAsync(new(step.StepId,step.TaskId,step.VehicleId!,"P1","WAIT",["E1"],["ATOMIC-BAY"],false,"concurrent fixture") {MapId="MULTI",MapVersionId="V1"},default);
var atomicReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var atomicArrivals=0;
var atomic=await Task.WhenAll(new[]{atomicA,atomicB}.Select(step=>Task.Run(async()=>
{
    using var connection=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"private.db")});
    var rival=new TrafficResourceManager(connection,catalog,new(TimeSpan.FromSeconds(1)));
    if(Interlocked.Increment(ref atomicArrivals)==2)atomicReady.SetResult();await atomicReady.Task;
    try{return (await rival.TryAcquireAsync(new(step.TaskId,step.VehicleId!,step.StepId,"MULTI","V1","WAIT","ATOMIC-BAY"),["ATOMIC-BAY"],default)).Acquired;}
    catch(InvalidOperationException e)when(e.InnerException?.Message.Contains("locked",StringComparison.OrdinalIgnoreCase)==true){return false;}
})));
Check(atomic.Count(success=>success)==1&&db.Queryable<TaskWaitingBayEntity>().Where(b=>b.ResourceId=="ATOMIC-BAY").Count()==1&&
    db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.ResourceId=="ATOMIC-BAY").Count()==1,"双连接并发选同一候车位只提交一个绑定及一个占用，无失败方残留");
DepartureReleaseChecks.Run(Check);
Console.WriteLine($"PASS {checks} waiting-bay/route/equipment checks. Evidence: {root}");

sealed class BayPlanner(MapDefinition map):IRoutePlanner
{
    public RoutePlan? Plan(string source,string destination,IReadOnlySet<string> unavailable)=>map.ToGraph().FindShortestTransportRoute(source,destination,unavailable);
}
