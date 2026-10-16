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
var root = Path.Combine(Path.GetTempPath(), "AGV-segment-commit-" + Guid.NewGuid().ToString("N"));
var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(root,"test.db") });
await new DatabaseInitializer(db).InitializeAsync(default);
var map = new MapDefinition("test","","LocalCartesian","m",
    [new("A",0,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P1"},new("B",2,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P2"},new("C",4,0,MapNodeType.Station,true,["FINAL"]){AgvSiteId="P3"}],
    [new("AB","A","B",2,1,true,true,["R","R2"]),new("BC","B","C",2,1,true,true,[])],
    [new("R",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)]),new("R2",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)]),new("FINAL",TrafficResourceType.Station,1,[new(3.5,-1),new(4.5,-1),new(4.5,1),new(3.5,1)])]);
var runtime = new ActiveMapRuntime();
var barrier = new Barrier(); var gate = new MapExecutionGate(barrier); gate.SetMode(MapExecutionMode.Running);
var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload("STEP","AGV","P1","P3",["P1","P2","P3"],null),new JsonSerializerOptions(JsonSerializerDefaults.Web));
var fixtureRoute = new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" };
var frozenJson = TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture("TASK","STEP","CMD","AGV",payload,new("M","V1",map,now),fixtureRoute));
var revision = TransportRouteSnapshots.Hash(frozenJson);
var first = new TrafficProgressFrame("TASK","STEP","AGV","STEP","CMD",revision,"M","V1","LocalCartesian","EPOCH",10,0,3,0,.95,.05,SignalQuality.Good,true,true,now.AddSeconds(-2));
var source = new Source { Evidence = new(first,first with { Sequence=11,ObservedAt=now.AddSeconds(-1) }) };
var envelope = new Envelope(); var policy = new TrafficSegmentReleasePolicy { Enabled=true };
var clock=new FixedClock(now);
var inspector = new TrafficSegmentReleaseInspector(db,runtime,gate,policy,source,envelope,clock);
var checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
void Reset()
{
    db.Deleteable<TrafficSegmentCommitLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficSegmentCommitEntity>().ExecuteCommand();db.Deleteable<TrafficLifecycleEventEntity>().ExecuteCommand();
    db.Deleteable<TrafficSegmentReceiptEventEntity>().ExecuteCommand();db.Deleteable<TrafficSegmentReceiptEntity>().ExecuteCommand();
    db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficRequestEntity>().ExecuteCommand();
    db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommand();db.Deleteable<DispatchTaskStepEntity>().ExecuteCommand();db.Deleteable<DispatchTaskEntity>().ExecuteCommand();
    db.Insertable(new DispatchTaskEntity { TaskId="TASK",StationId="ST",VehicleId="AGV",VendorTaskId="STEP",Stage=(int)DispatchTaskStage.GoingToPickup }).ExecuteCommand();
    db.Insertable(new DispatchTaskStepEntity { StepId="STEP",TaskId="TASK",StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Executing,VehicleId="AGV",VendorTaskId="STEP",SourceSite="P1",DestinationSite="P3" }).ExecuteCommand();
    db.Insertable(new AgvCommandOutboxEntity { CommandId="CMD",IdempotencyKey="STEP:STEP:TRANSPORT",TaskId="TASK",StepId="STEP",VehicleId="AGV",VendorCommandId="STEP",CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,PayloadJson=payload,ExecutionRouteSnapshotJson=frozenJson,ExecutionRouteSnapshotHash=revision,ExecutionMapId="M",ExecutionMapVersionId="V1",SentAt=now.AddSeconds(-3).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE2",ResourceId="R2",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="FINAL-LEASE",ResourceId="FINAL",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    clock.UtcNow=now;
    runtime.Replace(new("M","V1",map,now)); source.OnRead=null;source.Hang=false;source.Evidence=new(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});
    envelope.Body=new("CALIBRATED","AGV","LocalCartesian",.5,true,true);gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;policy.Enabled=true;
}
var options=new TrafficSegmentCommitOptions{Enabled=true};
var service=new TrafficSegmentReceiptService(db,inspector,gate,clock,options);
int LeaseCount()=>db.Queryable<TrafficResourceLeaseEntity>().Count();
int AuditCount()=>db.Queryable<TrafficSegmentCommitLeaseEntity>().Count();
int ResultCount()=>db.Queryable<TrafficSegmentCommitEntity>().Count();
void Latest()=>source.Evidence=new(first with{Sequence=12,ObservedAt=now.AddMilliseconds(-600)},first with{Sequence=13,ObservedAt=now.AddMilliseconds(-200)});
Reset();var issued=await service.IssueAsync("STEP",default);Check(issued.CandidateCount==2,"凭据仅包含已通过资源不包含终点");Latest();
var request=new TrafficSegmentCommitRequest(issued.ReceiptId!,"REQUEST");var outcome=await service.CommitAsync(request,default);
Check(outcome.Accepted&&outcome.ResourcesReleasedNow&&outcome.ReleasedLeaseIds.Count==2,"最新两帧证明后真实后台组件同事务释放两资源");
Check(LeaseCount()==1&&db.Queryable<TrafficResourceLeaseEntity>().First().ResourceId=="FINAL","终点租约保持直到正常交接");
Check(AuditCount()==2&&ResultCount()==1&&db.Queryable<TrafficSegmentReceiptEventEntity>().Count()==2,"逐资源审计结果与Committed事件完整保存");
Check(db.Queryable<TrafficLifecycleEventEntity>().Count()==1,"交通历史保存逐资源释放事件");
var remainingRequest=db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="STEP");
Check(remainingRequest.State=="Held"&&remainingRequest.ResourceIds=="[\"FINAL\"]","消费提交立即同步剩余占用请求");
Check((await service.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.Committed,"已消费凭据展示历史状态");
var replay=await service.CommitAsync(request,default);Check(replay.Accepted&&replay.Replayed&&!replay.ResourcesReleasedNow&&AuditCount()==2,"同请求重放不再次删除或写审计");
Check(!(await service.CommitAsync(request with{RequestId="OTHER-REQUEST"},default)).Accepted,"一个凭据禁止换请求号再消费");
Check(!(await service.CommitAsync(request with{ReceiptId="OTHER-RECEIPT"},default)).Accepted,"同请求号禁止绑定另一凭据");
db.Insertable(new TrafficResourceLeaseEntity{LeaseId="LATER",ResourceId="R",OwnerStepId="OTHER",TaskId="OTHER",VehicleId="OTHER",MapId="M",MapVersionId="V1",AcquiredAt=now.LocalDateTime}).ExecuteCommand();
options.Enabled=false;var restarted=new TrafficSegmentReceiptService(db,new TrafficSegmentReleaseInspector(db,runtime,gate,policy),gate,clock);
Check((await restarted.CommitAsync(request,default)).Replayed&&LeaseCount()==2,"新实例且开关关闭仅重放已提交结果不影响他车后来占用");
Check((await restarted.ReadCommittedAsync(request,default))!.ReleasedLeaseIds.Count==2,"提交结果可独立只读查询");options.Enabled=true;
var immutable=false;try{db.Updateable<TrafficSegmentCommitEntity>().Where(x=>true).SetColumns(x=>x.ResultHash=="FAKE").ExecuteCommand();}catch(Exception ex){immutable=ex.ToString().Contains("segment commit is immutable");}Check(immutable,"提交结果禁止改写");

async Task Deny(string name,Action change,bool newer=true)
{
    Reset();options.Enabled=true;policy.SafetyMarginMeters=.15;var receipt=await service.IssueAsync("STEP",default);if(newer)Latest();change();
    var result=await service.CommitAsync(new(receipt.ReceiptId!,Guid.NewGuid().ToString("N")),default);
    Check(!result.Accepted&&LeaseCount()==3&&AuditCount()==0&&ResultCount()==0,name+" 拒绝且保留全部租约");
}
await Deny("不重采旧样本",()=>{},false);
await Deny("最新控制器周期变化",()=>source.Evidence=source.Evidence! with{First=source.Evidence.First with{ControllerEpoch="NEW"},Second=source.Evidence.Second with{ControllerEpoch="NEW"}});
await Deny("最新进度倒退",()=>source.Evidence=source.Evidence! with{First=source.Evidence.First with{LastFullyTraversedEdgeIndex=-1,X=1},Second=source.Evidence.Second with{LastFullyTraversedEdgeIndex=-1,X=1}});
await Deny("最新车体回到资源内",()=>source.Evidence=source.Evidence! with{First=source.Evidence.First with{X=1.5},Second=source.Evidence.Second with{X=1.5}});
await Deny("最新证据离线",()=>source.Evidence=source.Evidence! with{Second=source.Evidence.Second with{Online=false}});
await Deny("最新核定包络改变",()=>envelope.Body=envelope.Body! with{RadiusFromReportedReferenceMeters=.6});
await Deny("原任务取消",()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.CancelRequestId=="CANCEL").ExecuteCommand());
await Deny("原租约重新取得",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>x.LeaseId=="LEASE").SetColumns(x=>x.AcquiredAt==now.LocalDateTime).ExecuteCommand());
await Deny("恢复门禁关闭",()=>barrier.IsOpen=false);
await Deny("活动地图变化",()=>runtime.Replace(new("M","V2",map,now)));
await Deny("凭据过期",()=>clock.UtcNow=now.AddSeconds(2));
await Deny("消费开关关闭",()=>options.Enabled=false);
await Deny("最新采集中取消竞态",()=>source.OnRead=()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.Stage==(int)DispatchTaskStage.CancellationPending).ExecuteCommand());

Reset();options.Enabled=true;issued=await service.IssueAsync("STEP",default);Latest();
var defaultClosed=new TrafficSegmentReceiptService(db,inspector,gate,clock);
Check(!(await defaultClosed.CommitAsync(new(issued.ReceiptId!,"DEFAULT"),default)).Accepted&&LeaseCount()==3,"默认配置关闭不消费");

Reset();issued=await service.IssueAsync("STEP",default);Latest();
db.Ado.ExecuteCommand("CREATE TRIGGER fail_commit_audit BEFORE INSERT ON traffic_segment_commit_lease WHEN NEW.ResourceId='R2' BEGIN SELECT RAISE(ABORT,'simulated commit audit failure'); END;");
request=new(issued.ReceiptId!,"ROLLBACK");outcome=await service.CommitAsync(request,default);
Check(!outcome.Accepted&&LeaseCount()==3&&AuditCount()==0&&ResultCount()==0&&db.Queryable<TrafficSegmentReceiptEventEntity>().Count()==1,"第二资源审计失败全部租约审计结果事件回滚");
db.Ado.ExecuteCommand("DROP TRIGGER fail_commit_audit");
Check((await service.CommitAsync(request,default)).Accepted&&AuditCount()==2,"原请求回滚后可重试且只保存完整提交");

Reset();issued=await service.IssueAsync("STEP",default);Latest();
db.Ado.ExecuteCommand("CREATE TRIGGER cancel_commit_tail AFTER INSERT ON traffic_segment_commit BEGIN UPDATE dispatch_task SET CancelRequestId='LATE-CANCEL' WHERE TaskId='TASK'; END;");
outcome=await service.CommitAsync(new(issued.ReceiptId!,"TAIL-CANCEL"),default);
Check(!outcome.Accepted&&LeaseCount()==3&&AuditCount()==0&&ResultCount()==0,"事务末尾原任务变化通过共用上下文核验回滚");
db.Ado.ExecuteCommand("DROP TRIGGER cancel_commit_tail");

Reset();issued=await service.IssueAsync("STEP",default);Latest();
var originalRequestResources=db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="STEP").ResourceIds;
db.Ado.ExecuteCommand("CREATE TRIGGER fail_remaining_request BEFORE UPDATE ON traffic_request WHEN NEW.StepId='STEP' BEGIN SELECT RAISE(ABORT,'simulated remaining request failure'); END;");
outcome=await service.CommitAsync(new(issued.ReceiptId!,"REQUEST-UPDATE-FAIL"),default);
Check(!outcome.Accepted&&LeaseCount()==3&&AuditCount()==0&&ResultCount()==0&&db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="STEP").ResourceIds==originalRequestResources,"剩余请求更新失败同事务回滚解锁审计及展示");
db.Ado.ExecuteCommand("DROP TRIGGER fail_remaining_request");

Reset();issued=await service.IssueAsync("STEP",default);Latest();
using(var during=new CancellationTokenSource())
{
    db.Aop.OnLogExecuted=(sql,args)=>{if(sql.TrimStart().StartsWith("DELETE",StringComparison.OrdinalIgnoreCase)&&sql.Contains("traffic_resource_lease",StringComparison.OrdinalIgnoreCase))during.Cancel();};
    var propagated=false;try{await service.CommitAsync(new(issued.ReceiptId!,"CANCEL-AFTER-DELETE"),during.Token);}catch(OperationCanceledException){propagated=true;}
    db.Aop.OnLogExecuted=null;
    Check(propagated&&LeaseCount()==3&&AuditCount()==0&&ResultCount()==0,"删除后调用方取消整笔回滚并传播取消");
}

Reset();issued=await service.IssueAsync("STEP",default);Latest();request=new(issued.ReceiptId!,"CONCURRENT");
Task<TrafficSegmentCommitResult> a;Task<TrafficSegmentCommitResult>b;
using(ExecutionContext.SuppressFlow()){a=Task.Run(()=>service.CommitAsync(request,default));b=Task.Run(()=>service.CommitAsync(request,default));}
var pair=await Task.WhenAll(a,b);
Check(pair.All(x=>x.Accepted)&&pair.Count(x=>x.Replayed)==1&&LeaseCount()==1&&AuditCount()==2&&ResultCount()==1,"并发原请求一次实际提交一次持久化重放");
Reset();issued=await service.IssueAsync("STEP",default);Latest();restarted=new TrafficSegmentReceiptService(db,inspector,gate,clock,options);
Check(!(await restarted.CommitAsync(new(issued.ReceiptId!,"OLD-SESSION"),default)).Accepted&&LeaseCount()==3,"重建服务拒绝未提交的旧会话凭据");
using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();var propagated=false;try{await service.CommitAsync(new(issued.ReceiptId!,"CANCEL"),cancelled.Token);}catch(OperationCanceledException){propagated=true;}Check(propagated&&LeaseCount()==3,"消费调用方取消传播且不删租约");}
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"commit-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="default-off production component, isolated SQLite and simulated evidence; no device connection or public IPC"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} commit checks. Database: {root}");
sealed class Barrier:IRecoveryBarrier{public bool IsOpen{get;set;}=true;}
sealed class FixedClock(DateTimeOffset now):TimeProvider{public DateTimeOffset UtcNow=now;public override DateTimeOffset GetUtcNow()=>UtcNow;}
sealed class Source:IAuthoritativeTrafficProgressReader
{
    public TrafficProgressPair? Evidence;public Action? OnRead;public bool Hang;
    public Task<TrafficProgressPair?> ReadAsync(string vehicleId,CancellationToken ct){OnRead?.Invoke();return Hang?new TaskCompletionSource<TrafficProgressPair?>().Task:Task.FromResult(Evidence);}
}
sealed class Envelope:ITrafficBodyEnvelopeProvider
{
    public TrafficBodyEnvelope? Body;
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId,CancellationToken ct)=>Task.FromResult(Body);
}
