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
Console.WriteLine($"PASS {checks} waiting-bay/route/equipment checks. Evidence: {root}");
