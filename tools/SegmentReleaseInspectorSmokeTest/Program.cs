using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(), "AGV-segment-inspector-" + Guid.NewGuid().ToString("N"));
var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(root,"test.db") });
db.CodeFirst.InitTables<DispatchTaskEntity,DispatchTaskStepEntity,AgvCommandOutboxEntity,TrafficRequestEntity,TrafficResourceLeaseEntity>();
var map = new MapDefinition("test","","LocalCartesian","m",
    [new("A",0,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P1"},new("B",2,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P2"},new("C",4,0,MapNodeType.Station,true,[]){AgvSiteId="P3"}],
    [new("AB","A","B",2,1,true,true,["R"]),new("BC","B","C",2,1,true,true,[])],
    [new("R",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)])]);
var runtime = new ActiveMapRuntime();
var barrier = new Barrier(); var gate = new MapExecutionGate(barrier); gate.SetMode(MapExecutionMode.Running);
var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload("STEP","AGV","P1","P3",["P1","P2","P3"],null),new JsonSerializerOptions(JsonSerializerDefaults.Web));
var fixtureRoute = new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" };
var frozenJson = TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture("TASK","STEP","CMD","AGV",payload,new("M","V1",map,now),fixtureRoute));
var revision = TransportRouteSnapshots.Hash(frozenJson);
var first = new TrafficProgressFrame("TASK","STEP","AGV","STEP","CMD",revision,"M","V1","LocalCartesian","EPOCH",10,0,3,0,.95,.05,SignalQuality.Good,true,true,now.AddSeconds(-2));
var source = new Source { Evidence = new(first,first with { Sequence=11,ObservedAt=now.AddSeconds(-1) }) };
var envelope = new Envelope(); var policy = new TrafficSegmentReleasePolicy { Enabled=true };
var inspector = new TrafficSegmentReleaseInspector(db,runtime,gate,policy,source,envelope,new FixedClock(now));
var checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
void Reset()
{
    db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficRequestEntity>().ExecuteCommand();
    db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommand();db.Deleteable<DispatchTaskStepEntity>().ExecuteCommand();db.Deleteable<DispatchTaskEntity>().ExecuteCommand();
    db.Insertable(new DispatchTaskEntity { TaskId="TASK",StationId="ST",VehicleId="AGV",VendorTaskId="STEP",Stage=(int)DispatchTaskStage.GoingToPickup }).ExecuteCommand();
    db.Insertable(new DispatchTaskStepEntity { StepId="STEP",TaskId="TASK",StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Executing,VehicleId="AGV",VendorTaskId="STEP",SourceSite="P1",DestinationSite="P3" }).ExecuteCommand();
    db.Insertable(new AgvCommandOutboxEntity { CommandId="CMD",IdempotencyKey="STEP:STEP:TRANSPORT",TaskId="TASK",StepId="STEP",VehicleId="AGV",VendorCommandId="STEP",CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,PayloadJson=payload,ExecutionRouteSnapshotJson=frozenJson,ExecutionRouteSnapshotHash=revision,ExecutionMapId="M",ExecutionMapVersionId="V1",SentAt=now.AddSeconds(-3).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    runtime.Replace(new("M","V1",map,now)); source.OnRead=null;source.Hang=false;source.Evidence=new(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});
    envelope.Body=new("CALIBRATED","AGV","LocalCartesian",.5,true,true);gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;policy.Enabled=true;
}
string Rows() => JsonSerializer.Serialize(new {
    Tasks=db.Queryable<DispatchTaskEntity>().ToList(),Steps=db.Queryable<DispatchTaskStepEntity>().ToList(),
    Commands=db.Queryable<AgvCommandOutboxEntity>().ToList(),Requests=db.Queryable<TrafficRequestEntity>().ToList(),Leases=db.Queryable<TrafficResourceLeaseEntity>().ToList() });
async Task Refuse(string name,Action mutate){Reset();mutate();var before=Rows();var result=await inspector.InspectAsync("STEP",default);Check(!result.Available||result.Candidates.All(c=>!c.EvidenceVerified),name);Check(!result.ResourcesReleased&&Rows()==before,name+" 保持所有记录");}
Reset();var baseline=Rows();var valid=await inspector.InspectAsync("STEP",default);
Check(valid.Available&&valid.Candidates.Single().EvidenceVerified,"数据库原运输及映射站点路径建立权威上下文");
Check(!valid.ResourcesReleased&&baseline==Rows(),"合格及过期租约只读评估均不写库不解锁");
Check(!(await new TrafficSegmentReleaseInspector(db,runtime,gate,policy).InspectAsync("STEP",default)).Available,"缺生产证据源明确不可用");
Check(!(await inspector.InspectAsync("UNKNOWN",default)).Available,"未知步骤不可自行构建上下文");
await Refuse("旧命令无发送快照",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.ExecutionRouteSnapshotJson==null).ExecuteCommand());
await Refuse("快照摘要损坏",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.ExecutionRouteSnapshotHash=="BAD").ExecuteCommand());
await Refuse("同版本地图几何发生变化",()=>runtime.Replace(new("M","V1",map with {Nodes=[map.Nodes[0],map.Nodes[1] with {Y=.1},map.Nodes[2]]},now)));
await Refuse("默认关闭",()=>policy.Enabled=false);
await Refuse("恢复门禁关闭",()=>barrier.IsOpen=false);
await Refuse("地图门禁关闭",()=>gate.SetMode(MapExecutionMode.Held));
await Refuse("原命令旧地图",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.ExecutionMapVersionId=="OLD").ExecuteCommand());
await Refuse("旧记录无地图",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.ExecutionMapId==null).ExecuteCommand());
await Refuse("租约其他车辆",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>true).SetColumns(x=>x.VehicleId=="OTHER").ExecuteCommand());
await Refuse("交通请求其他任务",()=>db.Updateable<TrafficRequestEntity>().Where(x=>true).SetColumns(x=>x.TaskId=="OTHER").ExecuteCommand());
await Refuse("步骤原运输编号不符",()=>db.Updateable<DispatchTaskStepEntity>().Where(x=>true).SetColumns(x=>x.VendorTaskId=="OTHER").ExecuteCommand());
await Refuse("取消请求已存在",()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.CancelRequestId=="CANCEL").ExecuteCommand());
await Refuse("步骤已完成",()=>db.Updateable<DispatchTaskStepEntity>().Where(x=>true).SetColumns(x=>x.Status==(int)DispatchTaskStepStatus.Completed).ExecuteCommand());
await Refuse("命令发送结果不确定",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.Status==(int)AgvCommandStatus.Sending).ExecuteCommand());
await Refuse("缺接收时刻",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.SentAt==null).ExecuteCommand());
await Refuse("路径被替换",()=>db.Updateable<TrafficRequestEntity>().Where(x=>true).SetColumns(x=>x.EdgeIds=="[\"BC\",\"AB\"]").ExecuteCommand());
await Refuse("异常路径JSON",()=>db.Updateable<TrafficRequestEntity>().Where(x=>true).SetColumns(x=>x.EdgeIds=="broken").ExecuteCommand());
await Refuse("未知请求起点",()=>db.Updateable<TrafficRequestEntity>().Where(x=>true).SetColumns(x=>x.Source=="UNKNOWN").ExecuteCommand());
await Refuse("无核定车辆包络",()=>envelope.Body=null);
Reset();source.OnRead=()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.Stage==(int)DispatchTaskStage.CancellationPending).ExecuteCommand();
Check(!(await inspector.InspectAsync("STEP",default)).Available,"采集中取消任务拒绝过时结果");
Reset();source.OnRead=()=>runtime.Replace(new("M","V1",map,now));
Check(!(await inspector.InspectAsync("STEP",default)).Available,"同版本地图实例替换仍要求重采集");
Reset();source.OnRead=()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>true).SetColumns(x=>x.AcquiredAt==now.LocalDateTime).ExecuteCommand();
Check(!(await inspector.InspectAsync("STEP",default)).Available,"租约同编号重建拒绝过时结果");
Reset();source.OnRead=()=>barrier.IsOpen=false;
Check(!(await inspector.InspectAsync("STEP",default)).Available,"采集中恢复屏障关闭拒绝结果");
Reset();source.OnRead=()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.PayloadJson==payload+" ").ExecuteCommand();
Check(!(await inspector.InspectAsync("STEP",default)).Available,"采集中原命令载荷变化拒绝结果");
Reset();source.OnRead=()=>db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();
Check(!(await inspector.InspectAsync("STEP",default)).Available,"采集中租约删除拒绝结果");
foreach(var transport in new[]{(DispatchTaskStepType.DeliverFullBoxToDumper,DispatchTaskStage.GoingToDumper),(DispatchTaskStepType.EnterDumpingPosition,DispatchTaskStage.Dumping),(DispatchTaskStepType.ReturnEmptyBox,DispatchTaskStage.ReturningEmptyBox)})
{
    Reset();db.Updateable<DispatchTaskStepEntity>().Where(x=>true).SetColumns(x=>x.StepType==(int)transport.Item1).ExecuteCommand();
    db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.Stage==(int)transport.Item2).ExecuteCommand();
    Check((await inspector.InspectAsync("STEP",default)).Candidates.Single().EvidenceVerified,"运输步骤与正确阶段对应 "+transport.Item1);
}
Reset();source.OnRead=()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>true).SetColumns(x=>x.RenewedAt==now.LocalDateTime).ExecuteCommand();
Check((await inspector.InspectAsync("STEP",default)).Candidates.Single().EvidenceVerified,"正常心跳续期不改变归属证明");
Reset();source.OnRead=()=>throw new IOException("模拟源断连");
Check(!(await inspector.InspectAsync("STEP",default)).Available,"只读证据源断连明确不可用");
Reset();source.Hang=true;baseline=Rows();
Check(!(await inspector.InspectAsync("STEP",default)).Available&&Rows()==baseline,"适配器不响应取消仍在5秒超时且不写库");
using(var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,default))Check(access is not null,"采集失败或超时不持有控制门禁");
Reset();using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await inspector.InspectAsync("STEP",cancelled.Token);Check(false,"取消应传播");}catch(OperationCanceledException){Check(true,"调用方取消正常传播");}}
Check(revision!=TransportRouteSnapshots.Hash(TransportRouteSnapshots.Serialize(JsonSerializer.Deserialize<TransportRouteSnapshot>(frozenJson,new JsonSerializerOptions(JsonSerializerDefaults.Web))! with {CommandId="CMD2"})),"路线摘要绑定原命令");
Check(revision!=TransportRouteSnapshots.Hash(TransportRouteSnapshots.Serialize(JsonSerializer.Deserialize<TransportRouteSnapshot>(frozenJson,new JsonSerializerOptions(JsonSerializerDefaults.Web))! with {MapVersionId="V2"})),"路线摘要绑定地图版本");
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"inspector-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,ResourcesReleased=false,Scope="read-only persisted context inspector; simulated evidence"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} inspector checks. Database: {root}");

sealed class Barrier:IRecoveryBarrier{public bool IsOpen{get;set;}=true;}
sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
sealed class Source:IAuthoritativeTrafficProgressReader
{
    public TrafficProgressPair? Evidence;public Action? OnRead;public bool Hang;
    public Task<TrafficProgressPair?> ReadAsync(string vehicleId,CancellationToken ct){OnRead?.Invoke();return Hang ? new TaskCompletionSource<TrafficProgressPair?>().Task : Task.FromResult(Evidence);}
}
sealed class Envelope:ITrafficBodyEnvelopeProvider
{
    public TrafficBodyEnvelope? Body;
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId,CancellationToken ct)=>Task.FromResult(Body);
}
