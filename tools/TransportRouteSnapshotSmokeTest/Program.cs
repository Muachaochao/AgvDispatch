using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

var root=Path.Combine(Path.GetTempPath(),"AGV-route-snapshot-"+Guid.NewGuid().ToString("N"));
var options=new DatabaseOptions{DatabasePath=Path.Combine(root,"test.db")};var db=DatabaseFactory.Create(options);
await new DatabaseInitializer(db).InitializeAsync(default);
var map=new MapDefinition("test","","LocalCartesian","m",
    [new("A",0,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P1"},new("B",2,0,MapNodeType.Station,true,[]){AgvSiteId="P2"}],
    [new("AB","A","B",2,1,false,true,[])],[]);
var active=new ActiveMapRuntime();active.Replace(new("M","V1",map,DateTimeOffset.UtcNow));
var repository=new AgvCommandOutboxRepository(db,active);var checks=0;
void Check(bool good,string name){if(!good)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
void Seed(string id,bool wrong=false)
{
    db.Insertable(new DispatchTaskEntity{TaskId=id,StationId=id,Stage=(int)DispatchTaskStage.WaitingForVehicle}).ExecuteCommand();
    db.Insertable(new DispatchTaskStepEntity{StepId=id+"-STEP",TaskId=id,Status=(int)DispatchTaskStepStatus.Ready,StepType=(int)DispatchTaskStepType.GoToPickup,Sequence=1}).ExecuteCommand();
    db.Insertable(new TrafficRequestEntity{StepId=id+"-STEP",TaskId=id,VehicleId="AGV",Source="A",Destination="B",MapId="M",MapVersionId=wrong?"OLD":"V1",EdgeIds="[\"AB\"]",ResourceIds="[]",State="Acquired"}).ExecuteCommand();
}
Seed("TASK");Check(await repository.TryPrepareTransportStepAsync("TASK","TASK-STEP","AGV",["P1","P2"],null,default),"准备事务写入命令及路线快照");
var command=db.Queryable<AgvCommandOutboxEntity>().Where(x=>x.TaskId=="TASK").Single();
var snapshot=TransportRouteSnapshots.Read(command);var originalJson=command.ExecutionRouteSnapshotJson;var originalHash=command.ExecutionRouteSnapshotHash;
Check(snapshot.NodeIds.SequenceEqual(["A","B"])&&snapshot.EdgeIds.SequenceEqual(["AB"])&&snapshot.AgvSites.SequenceEqual(["P1","P2"]),"原逻辑节点/路段和Robokit站点分别持久化");
Check(snapshot.MapId=="M"&&snapshot.MapVersionId=="V1"&&snapshot.CommandId==command.CommandId&&snapshot.StepId=="TASK-STEP","快照绑定地图版本及稳定命令步骤");
Check(snapshot.PayloadHash==TransportRouteSnapshots.Hash(command.PayloadJson),"快照包含原载荷摘要");
Check(!await repository.TryPrepareTransportStepAsync("TASK","TASK-STEP","AGV",["P1","P2"],null,default),"重复准备不会重写快照或新增命令");
var claim=await repository.TryClaimAsync(command.CommandId,DateTimeOffset.Now.AddSeconds(1),default);Check(claim is not null,"数据库保护允许认领命令");
await repository.ScheduleRetryAsync(command.CommandId,"模拟响应丢失",DateTimeOffset.Now.AddSeconds(-1),5,default);
Check((await repository.TryClaimAsync(command.CommandId,DateTimeOffset.Now.AddSeconds(1),default)) is not null,"数据库保护允许稳定命令重试");
await repository.MarkSentAsync(command.CommandId,"TASK-STEP",default);
command=db.Queryable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).Single();
Check(command.Status==(int)AgvCommandStatus.Sent&&command.AttemptCount==2,"确认更新与尝试次数通过保护");
Check(command.ExecutionRouteSnapshotJson==originalJson&&command.ExecutionRouteSnapshotHash==originalHash,"响应丢失重试及确认不改变快照");
void Guard(string name,Action change)
{
    var refused=false;try{change();}catch(Exception ex){refused=ex.ToString().Contains("transport route snapshot is immutable");}
    Check(refused,name);
    var row=db.Queryable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).Single();
    Check(row.ExecutionRouteSnapshotJson==originalJson&&row.ExecutionRouteSnapshotHash==originalHash,name+" 原快照保留");
}
Guard("禁止直接覆盖快照",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).SetColumns(x=>x.ExecutionRouteSnapshotJson=="{}").ExecuteCommand());
Guard("禁止清空摘要",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).SetColumns(x=>x.ExecutionRouteSnapshotHash==null).ExecuteCommand());
Guard("禁止修改原载荷",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).SetColumns(x=>x.PayloadJson==command.PayloadJson+" ").ExecuteCommand());
Guard("禁止修改原车号",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).SetColumns(x=>x.VehicleId=="OTHER").ExecuteCommand());
Guard("禁止补成新版地图",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).SetColumns(x=>x.ExecutionMapVersionId=="V2").ExecuteCommand());
db.Updateable<TrafficRequestEntity>().Where(x=>x.TaskId=="TASK").SetColumns(x=>x.EdgeIds=="[]").ExecuteCommand();
Check(TransportRouteSnapshots.Read(db.Queryable<AgvCommandOutboxEntity>().Where(x=>x.CommandId==command.CommandId).Single()).EdgeIds.SequenceEqual(["AB"]),"后续路径观察变化不能覆盖命令原路段");
Seed("BAD",true);var rejected=false;
try{await repository.TryPrepareTransportStepAsync("BAD","BAD-STEP","AGV",["P1","P2"],null,default);}catch(InvalidOperationException){rejected=true;}
Check(rejected&&!db.Queryable<AgvCommandOutboxEntity>().Any(x=>x.TaskId=="BAD"),"地图版本不符准备回滚且无命令");
Check(db.Queryable<DispatchTaskEntity>().Single(x=>x.TaskId=="BAD").Stage==(int)DispatchTaskStage.WaitingForVehicle&&db.Queryable<DispatchTaskStepEntity>().Single(x=>x.TaskId=="BAD").Status==(int)DispatchTaskStepStatus.Ready,"快照失败同事务回滚任务步骤");
Seed("LEGACY");var legacyRepository=new AgvCommandOutboxRepository(db);
Check(await legacyRepository.TryPrepareTransportStepAsync("LEGACY","LEGACY-STEP","AGV",["P1","P2"],null,default),"无活动地图兼容准备但不生成伪快照");
var legacy=db.Queryable<AgvCommandOutboxEntity>().Single(x=>x.TaskId=="LEGACY");
Check(legacy.ExecutionRouteSnapshotJson is null&&legacy.ExecutionRouteSnapshotHash is null,"遗留无快照保持未知");
var oldRefused=false;try{TransportRouteSnapshots.Read(legacy);}catch(InvalidDataException){oldRefused=true;}Check(oldRefused,"遗留记录不能通过快照核验");
var backfillRefused=false;try{db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.TaskId=="LEGACY").SetColumns(x=>x.ExecutionRouteSnapshotJson==originalJson).ExecuteCommand();}catch(Exception ex){backfillRefused=ex.ToString().Contains("transport route snapshot is immutable");}Check(backfillRefused,"禁止事后补造遗留快照");
Seed("NO-RESOURCES");
db.Updateable<TrafficRequestEntity>().Where(x=>x.StepId=="NO-RESOURCES-STEP").SetColumns(x=>x.State=="NotRequired").ExecuteCommand();
Check(await repository.TryPrepareTransportStepAsync("NO-RESOURCES","NO-RESOURCES-STEP","AGV",["P1","P2"],null,default),"无资源路径NotRequired仍生成完整发送快照");
foreach(var nodeResource in new[]{false,true})
{
    var id=nodeResource?"HIDDEN-NODE-RESOURCE":"HIDDEN-EDGE-RESOURCE";Seed(id);
    db.Updateable<TrafficRequestEntity>().Where(x=>x.TaskId==id).SetColumns(x=>x.State=="NotRequired").ExecuteCommand();
    var protectedMap=nodeResource?map with{Nodes=map.Nodes.Select(n=>n.Id=="B"?n with{ResourceIds=["R"]}:n).ToArray(),Resources=[new("R",TrafficResourceType.Station)]}:map with{Edges=map.Edges.Select(e=>e with{ResourceIds=["R"]}).ToArray(),Resources=[new("R",TrafficResourceType.NarrowAisle)]};
    active.Replace(new("M","V1",protectedMap,DateTimeOffset.UtcNow));
    var blocked=false;try{await repository.TryPrepareTransportStepAsync(id,id+"-STEP","AGV",["P1","P2"],null,default);}catch(InvalidOperationException){blocked=true;}
    Check(blocked&&!db.Queryable<AgvCommandOutboxEntity>().Any(x=>x.TaskId==id),"NotRequired不能绕过真实"+(nodeResource?"节点":"路段")+"资源且不留下发送命令");
}
active.Replace(new("M","V1",map,DateTimeOffset.UtcNow));
var restarted=DatabaseFactory.Create(options);await new DatabaseInitializer(restarted).InitializeAsync(default);
Check(TransportRouteSnapshots.Read(restarted.Queryable<AgvCommandOutboxEntity>().Single(x=>x.TaskId=="TASK")).EdgeIds.SequenceEqual(["AB"]),"重新初始化和新连接保留原快照及保护");
Check(restarted.Queryable<AgvCommandOutboxEntity>().Single(x=>x.TaskId=="LEGACY").ExecutionRouteSnapshotJson is null,"初始化不回填历史");
var migrationOptions=new DatabaseOptions{DatabasePath=Path.Combine(root,"pre-snapshot.db")};
var migrating=DatabaseFactory.Create(migrationOptions);
migrating.CodeFirst.InitTables<LegacyOutboxEntity>();
migrating.Insertable(new LegacyOutboxEntity{CommandId="OLD-CMD",IdempotencyKey="OLD",TaskId="OLD-TASK",StepId="OLD-STEP",VehicleId="OLD-AGV",PayloadJson="old-payload",Status=(int)AgvCommandStatus.Sending,AttemptCount=3,ExecutionMapId="OLD-M",ExecutionMapVersionId="OLD-V"}).ExecuteCommand();
await new DatabaseInitializer(migrating).InitializeAsync(default);
var migrated=migrating.Queryable<AgvCommandOutboxEntity>().Single(x=>x.CommandId=="OLD-CMD");
Check(migrated.ExecutionRouteSnapshotJson is null&&migrated.ExecutionRouteSnapshotHash is null,"旧结构升级增加nullable字段且不补造快照");
Check(migrated.PayloadJson=="old-payload"&&migrated.Status==(int)AgvCommandStatus.Sending&&migrated.AttemptCount==3&&migrated.ExecutionMapVersionId=="OLD-V","升级保留原载荷发送状态次数及旧地图");
var migrationGuard=false;try{migrating.Updateable<AgvCommandOutboxEntity>().Where(x=>x.CommandId=="OLD-CMD").SetColumns(x=>x.ExecutionRouteSnapshotHash=="FAKE").ExecuteCommand();}catch(Exception ex){migrationGuard=ex.ToString().Contains("transport route snapshot is immutable");}Check(migrationGuard,"旧结构升级后同样阻止伪摘要回填");
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"snapshot-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="SQLite preparation, retry, rollback and immutable update guard; no device I/O"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} route snapshot checks. Database: {root}");
