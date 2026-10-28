using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

var now=DateTimeOffset.UtcNow;
var root=Path.Combine(Path.GetTempPath(),"AGV-bay-departure-"+Guid.NewGuid().ToString("N"));
var db=DatabaseFactory.Create(new DatabaseOptions{DatabasePath=Path.Combine(root,"test.db")});
db.CodeFirst.InitTables<DispatchTaskEntity,DispatchTaskStepEntity,AgvCommandOutboxEntity,TrafficRequestEntity,TrafficResourceLeaseEntity>();
db.CodeFirst.InitTables<TaskWaitingBayEntity>();
var map=new MapDefinition("synthetic departure","","LocalCartesian","m",
 [new("A",0,0,MapNodeType.Waypoint,true,["BAY"]){AgvSiteId="WAIT"},new("B",2,0,MapNodeType.Waypoint,true,[]){AgvSiteId="MID"},new("C",4,0,MapNodeType.Station,true,["DUMP"]){AgvSiteId="DUMP-SITE"}],
 [new("AB","A","B",2,1,true,true,[]),new("BC","B","C",2,1,true,true,[])],
 [new("BAY",TrafficResourceType.Station,1,[new(-1,-1),new(1.4,-1),new(1.4,1),new(-1,1)]),new("DUMP",TrafficResourceType.Station,1,[new(3.5,-1),new(4.5,-1),new(4.5,1),new(3.5,1)])]);
var payload=JsonSerializer.Serialize(new AgvTransportCommandPayload("ENTRY","AGV","WAIT","DUMP-SITE",["WAIT","MID","DUMP-SITE"],null),new JsonSerializerOptions(JsonSerializerDefaults.Web));
var request=new TrafficRequestEntity{StepId="ENTRY",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"BAY\",\"DUMP\"]",MapId="M",MapVersionId="V1",State="Held"};
var snapshot=TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture("TASK","ENTRY","CMD","AGV",payload,new("M","V1",map,now),request));
var revision=TransportRouteSnapshots.Hash(snapshot);
TrafficSegmentLease Lease(string id,string resource,string step="ENTRY")=>new(id,resource,"TASK",step,"AGV","M","V1");
TrafficWaitingBayLease Timed(TrafficSegmentLease l)=>new(l,now.AddSeconds(20));
var own=new[]{Lease("CURRENT-BAY","BAY"),Lease("CURRENT-DUMP","DUMP")};
var body=new TrafficBodyEnvelope("CERT","AGV","LocalCartesian",.5,true,true);
var ctx=new TrafficSegmentReleaseContext("TASK","ENTRY","AGV","ENTRY","CMD",revision,"M","V1","LocalCartesian",now.AddSeconds(-3),true,true,true,map,["A","B","C"],["AB","BC"],own,body);
var context=new TrafficWaitingBayDepartureContext(ctx,"A","C","BAY",[Timed(Lease("PRIOR-BAY","BAY","DELIVERY")),Timed(own[0])],own.Select(Timed).ToArray());
var first=new TrafficProgressFrame("TASK","ENTRY","AGV","ENTRY","CMD",revision,"M","V1","LocalCartesian","SESSION",10,0,3,0,.95,.05,SignalQuality.Good,true,true,now.AddSeconds(-2));
var pair=new TrafficProgressPair(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});var policy=new TrafficSegmentReleasePolicy{Enabled=true};
var checks=0;var report=new List<object>();
void Check(bool passed,string name){if(!passed)throw new Exception(name);checks++;report.Add(new{Name=name,Passed=true});Console.WriteLine("PASS "+name);}
TrafficWaitingBayDepartureAssessment Eval(TrafficWaitingBayDepartureContext? c=null,TrafficProgressPair? p=null)=>TrafficWaitingBayDepartureEvaluator.Evaluate(c??context,p??pair,policy,now);
void Deny(string name,TrafficWaitingBayDepartureContext? c=null,TrafficProgressPair? p=null){var r=Eval(c,p);Check(!r.DepartureEvidenceVerified&&!r.ResourcesReleased&&!r.ReleaseAuthorized,name);}
var valid=Eval();Check(valid.Available&&valid.DepartureEvidenceVerified&&valid.LeaseIds.Count==2,"两帧全车离位及后续路权通过，列出两个原候车租约");
Check(!valid.ResourcesReleased&&!valid.ReleaseAuthorized,"证据通过仍未释放也不授权");
Deny("仅车中心出区，车体尾部仍覆盖",p:pair with{First=first with{X=2},Second=pair.Second with{X=2}});
Deny("边界抖动，一帧覆盖不得通过",p:pair with{First=first with{X=2.05}});
Deny("包络安全余量恰好接触边界",p:pair with{First=first with{X=2.1}});
foreach(var f in new[]{pair.Second with{Online=false},pair.Second with{ObservedAt=now.AddSeconds(-8)},pair.Second with{ObservedAt=now.AddSeconds(1)},pair.Second with{Quality=SignalQuality.Bad},pair.Second with{LocalizationConfidence=.1},pair.Second with{ControllerEpoch="RESTART"},pair.Second with{Sequence=30},pair.Second with{Sequence=10},pair.Second with{MapVersionId="OLD"},pair.Second with{TaskId="OTHER"},pair.Second with{CommandId="OTHER"},pair.Second with{X=double.NaN}})Deny("离位坏样本拒绝 "+$"{f.Sequence}/{f.X}/{f.Online}/{f.MapVersionId}/{f.CommandId}",p:pair with{Second=f});
Deny("缺后续倒料资源",context with{Entry=ctx with{Leases=[own[0]]},EntryLeases=[Timed(own[0])]});
Deny("后续资源只有名字没有地图定义",context with{Entry=ctx with{Map=map with{Resources=[map.Resources[0]]}}});
Deny("后续资源边界缺失",context with{Entry=ctx with{Map=map with{Resources=[map.Resources[0],map.Resources[1] with{Boundary=null}]}}});
Deny("后续资源原租约编号缺失",context with{Entry=ctx with{Leases=[own[0],own[1] with{LeaseId=""}]},EntryLeases=[Timed(own[0]),Timed(own[1] with{LeaseId=""})]});
Deny("后续资源心跳过期",context with{EntryLeases=[Timed(own[0]),new(own[1],now.AddSeconds(-1))]});
Deny("历史候车租约过期仍保留",context with{BayLeases=[context.BayLeases[0] with{ExpiresAt=now},context.BayLeases[1]]});
Deny("同资源混入其他任务",context with{BayLeases=[context.BayLeases[0] with{Identity=context.BayLeases[0].Identity with{TaskId="OTHER"}},context.BayLeases[1]]});
Deny("同资源混入旧地图",context with{BayLeases=[context.BayLeases[0] with{Identity=context.BayLeases[0].Identity with{MapVersionId="OLD"}},context.BayLeases[1]]});
Deny("没有原入站候车租约",context with{BayLeases=[context.BayLeases[0]]});
Deny("重复原租约",context with{BayLeases=[context.BayLeases[0],context.BayLeases[0]]});
Deny("容量2不能作为单候车位",context with{Entry=ctx with{Map=map with{Resources=[map.Resources[0] with{Capacity=2},map.Resources[1]]}}});
Deny("等待/倒料同点拒绝",context with{DumpingNodeId="A"});
Deny("非原入站起点拒绝",context with{WaitingNodeId="B"});
Deny("缺车体资料",context with{Entry=ctx with{Body=null}});
Deny("取消/非执行状态",context with{Entry=ctx with{TaskAndStepExecuting=false}});
Deny("地图恢复门禁关闭",context with{Entry=ctx with{RecoveryAndMapGateOpen=false}});
Deny("未注册权威源",context with{Entry=ctx with{EvidenceSourceRegistered=false}});
Deny("未完全通过最后使用路段",p:pair with{First=first with{LastFullyTraversedEdgeIndex=-1,X=1},Second=pair.Second with{LastFullyTraversedEdgeIndex=-1,X=1}});
Deny("未来重入候车位不能提前释放",context with{Entry=ctx with{Map=map with{Edges=[map.Edges[0],map.Edges[1] with{ResourceIds=["BAY"]}]}}});

var runtime=new ActiveMapRuntime();var barrier=new Barrier();var gate=new MapExecutionGate(barrier);var clock=new FixedClock(now);
var reader=new TrafficSegmentReleaseInspector(db,runtime,gate,new()); // Commit/segment release policy stays disabled.
var source=new Source();var profile=new Profile();var sites=new TrafficWaitingBaySites("WAIT","DUMP-SITE");
var inspector=new TrafficWaitingBayDepartureInspector(db,reader,gate,()=>sites,source,profile,clock);var query=new TrafficWaitingBayDepartureQuery("ENTRY","BAY");
void Reset(){
 db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficRequestEntity>().ExecuteCommand();db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommand();db.Deleteable<DispatchTaskStepEntity>().ExecuteCommand();db.Deleteable<DispatchTaskEntity>().ExecuteCommand();
 db.Insertable(new DispatchTaskEntity{TaskId="TASK",StationId="ST",VehicleId="AGV",VendorTaskId="ENTRY",Stage=(int)DispatchTaskStage.Dumping}).ExecuteCommand();
 db.Insertable(new[]{new DispatchTaskStepEntity{StepId="ENTRY",TaskId="TASK",StepType=(int)DispatchTaskStepType.EnterDumpingPosition,Status=(int)DispatchTaskStepStatus.Executing,VehicleId="AGV",VendorTaskId="ENTRY",SourceSite="WAIT",DestinationSite="DUMP-SITE"},new DispatchTaskStepEntity{StepId="DELIVERY",TaskId="TASK",StepType=(int)DispatchTaskStepType.DeliverFullBoxToDumper,Status=(int)DispatchTaskStepStatus.Completed,VehicleId="AGV"}}).ExecuteCommand();
 db.Insertable(new AgvCommandOutboxEntity{CommandId="CMD",IdempotencyKey="ENTRY:TRANSPORT",TaskId="TASK",StepId="ENTRY",VehicleId="AGV",VendorCommandId="ENTRY",CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,PayloadJson=payload,ExecutionRouteSnapshotJson=snapshot,ExecutionRouteSnapshotHash=revision,ExecutionMapId="M",ExecutionMapVersionId="V1",SentAt=now.AddSeconds(-3).LocalDateTime}).ExecuteCommand();
 db.Insertable(request).ExecuteCommand();
 db.Insertable(context.BayLeases.Concat(context.EntryLeases.Where(l=>l.Identity.ResourceId!="BAY")).Select(l=>new TrafficResourceLeaseEntity{LeaseId=l.Identity.LeaseId,ResourceId=l.Identity.ResourceId,TaskId="TASK",OwnerStepId=l.Identity.StepId,VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(20).LocalDateTime}).ToArray()).ExecuteCommand();
 runtime.Replace(new("M","V1",map,now));gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;source.Evidence=pair;source.OnRead=null;source.Hang=false;profile.Value=new(body,.05,"REV",now.AddMinutes(1));profile.OnRead=null;profile.Reads=0;sites=new("WAIT","DUMP-SITE");
}
string Rows()=>JsonSerializer.Serialize(new{Tasks=db.Queryable<DispatchTaskEntity>().ToList(),Steps=db.Queryable<DispatchTaskStepEntity>().OrderBy(x=>x.StepId).ToList(),Outbox=db.Queryable<AgvCommandOutboxEntity>().ToList(),Requests=db.Queryable<TrafficRequestEntity>().ToList(),Leases=db.Queryable<TrafficResourceLeaseEntity>().OrderBy(x=>x.LeaseId).ToList()});
async Task Reject(string name,Action change){Reset();change();var before=Rows();var r=await inspector.ReadAsync(query,default);Check(!r.DepartureEvidenceVerified&&Rows()==before&&!r.ResourcesReleased&&!r.ReleaseAuthorized,name+"，评估无写入");}
Reset();var baseline=Rows();var pass=await inspector.ReadAsync(query,default);Check(pass.DepartureEvidenceVerified&&pass.LeaseIds.Count==2&&Rows()==baseline,"数据库原入站/历史候车租约与有效资料通过，三原租约不删");
Reset();var missing=await new TrafficWaitingBayDepartureInspector(db,reader,gate,()=>sites,clock:clock).ReadAsync(query,default);Check(!missing.Available&&missing.Reason.Contains("进度源"),"生产未注册源如实显示缺资料");
await Reject("候车心跳过期",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId=="PRIOR-BAY").SetColumns(l=>l.ExpiresAt==now.AddSeconds(-1).LocalDateTime).ExecuteCommand());
await Reject("后续心跳过期",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(l=>l.ResourceId=="DUMP").SetColumns(l=>l.ExpiresAt==now.AddSeconds(-1).LocalDateTime).ExecuteCommand());
await Reject("取消已记录",()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.CancelRequestId=="CANCEL").ExecuteCommand());
await Reject("仅发送意图未获返回",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.Status==(int)AgvCommandStatus.Sending).ExecuteCommand());
await Reject("不是原入站步骤",()=>db.Updateable<DispatchTaskStepEntity>().Where(x=>x.StepId=="ENTRY").SetColumns(x=>x.StepType==(int)DispatchTaskStepType.ReturnEmptyBox).ExecuteCommand());
await Reject("历史候车租约步骤缺失",()=>db.Deleteable<DispatchTaskStepEntity>().Where(x=>x.StepId=="DELIVERY").ExecuteCommand());
await Reject("历史候车租约步骤其他车",()=>db.Updateable<DispatchTaskStepEntity>().Where(x=>x.StepId=="DELIVERY").SetColumns(x=>x.VehicleId=="OTHER").ExecuteCommand());
await Reject("配置等待编号不唯一",()=>sites=new("UNKNOWN","DUMP-SITE"));
await Reject("核定资料过期",()=>profile.Value=profile.Value! with{VerifiedUntil=now});
await Reject("核定定位误差界扩大后仍覆盖",()=>{profile.Value=profile.Value! with{PositionUncertaintyMeters=.2};source.Evidence=pair with{First=first with{X=2.2,PositionUncertaintyMeters=.01},Second=pair.Second with{X=2.2,PositionUncertaintyMeters=.01}};});
await Reject("样本断连",()=>source.Evidence=pair with{Second=pair.Second with{Online=false}});
await Reject("源读取异常",()=>source.OnRead=()=>throw new IOException("模型断连"));
await Reject("未知边界",()=>runtime.Replace(new("M","V1",map with{Resources=[map.Resources[0] with{Boundary=null},map.Resources[1]]},now)));
Reset();source.OnRead=()=>db.Updateable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId=="PRIOR-BAY").SetColumns(l=>l.AcquiredAt==now.LocalDateTime).ExecuteCommand();Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间历史步骤租约重建不可沿用");
Reset();source.OnRead=()=>db.Deleteable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId=="PRIOR-BAY").ExecuteCommand();Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间历史步骤租约删除不可沿用");
Reset();source.OnRead=()=>runtime.Replace(new("M","V1",map,now));Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间同版本地图替换失效");
Reset();source.OnRead=()=>sites=new("A","C");Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间配置语义相同但编号变化仍重查");
Reset();source.OnRead=()=>barrier.IsOpen=false;Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间恢复门关闭失效");
Reset();source.OnRead=()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.CancelRequestId=="CANCEL").ExecuteCommand();Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间任务取消失效");
Reset();source.OnRead=()=>db.Deleteable<TrafficResourceLeaseEntity>().Where(l=>l.ResourceId=="DUMP").ExecuteCommand();Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间后续倒料资源丢失失效");
Reset();source.OnRead=()=>db.Updateable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId=="PRIOR-BAY").SetColumns(l=>l.ExpiresAt==now.LocalDateTime).ExecuteCommand();Check(!(await inspector.ReadAsync(query,default)).DepartureEvidenceVerified,"采集期间旧候车租约过期不通过");
Reset();profile.OnRead=()=>{if(profile.Reads==2)profile.Value=profile.Value! with{Revision="NEW"};};Check(!(await inspector.ReadAsync(query,default)).Available,"采集期间核定资料修订失效");
Reset();source.OnRead=()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>true).SetColumns(x=>x.RenewedAt==now.LocalDateTime).ExecuteCommand();Check((await inspector.ReadAsync(query,default)).DepartureEvidenceVerified,"正常续期不当成原归属改变");
Reset();source.Hang=true;baseline=Rows();Check(!(await inspector.ReadAsync(query,default)).Available&&Rows()==baseline,"不响应适配器5秒超时退出且不写库");
Reset();using(var cts=new CancellationTokenSource()){cts.Cancel();try{await inspector.ReadAsync(query,cts.Token);Check(false,"取消应传播");}catch(OperationCanceledException){Check(true,"调用方取消传播");}}
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"waiting-bay-departure-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="read-only synthetic evidence and private SQLite; not firmware or physical acceptance",Results=report,Examples=new{Passed=pass,MissingEvidence=missing}},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
Console.WriteLine($"PASS {checks} waiting bay departure checks. Database: {root}");
sealed class Barrier:IRecoveryBarrier{public bool IsOpen{get;set;}=true;}
sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
sealed class Source:IAuthoritativeTrafficProgressReader{public TrafficProgressPair? Evidence;public Action? OnRead;public bool Hang;public Task<TrafficProgressPair?> ReadAsync(string id,CancellationToken ct){OnRead?.Invoke();return Hang?new TaskCompletionSource<TrafficProgressPair?>().Task:Task.FromResult(Evidence);}}
sealed class Profile:ITrafficGeometryProfileProvider{public TrafficGeometryCertifiedProfile? Value;public Action? OnRead;public int Reads;public Task<TrafficGeometryCertifiedProfile?> ReadAsync(string id,CancellationToken ct){Reads++;OnRead?.Invoke();return Task.FromResult(Value);}}

