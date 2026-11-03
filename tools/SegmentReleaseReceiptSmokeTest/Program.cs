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
var root = Path.Combine(Path.GetTempPath(), "AGV-segment-receipt-" + Guid.NewGuid().ToString("N"));
var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(root,"test.db") });
await new DatabaseInitializer(db).InitializeAsync(default);
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
var clock=new FixedClock(now);
var inspector = new TrafficSegmentReleaseInspector(db,runtime,gate,policy,source,envelope,clock);
var checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
void Reset()
{
    db.Deleteable<TrafficSegmentReceiptEventEntity>().ExecuteCommand();db.Deleteable<TrafficSegmentReceiptEntity>().ExecuteCommand();
    db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficRequestEntity>().ExecuteCommand();
    db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommand();db.Deleteable<DispatchTaskStepEntity>().ExecuteCommand();db.Deleteable<DispatchTaskEntity>().ExecuteCommand();
    db.Insertable(new DispatchTaskEntity { TaskId="TASK",StationId="ST",VehicleId="AGV",VendorTaskId="STEP",Stage=(int)DispatchTaskStage.GoingToPickup }).ExecuteCommand();
    db.Insertable(new DispatchTaskStepEntity { StepId="STEP",TaskId="TASK",StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Executing,VehicleId="AGV",VendorTaskId="STEP",SourceSite="P1",DestinationSite="P3" }).ExecuteCommand();
    db.Insertable(new AgvCommandOutboxEntity { CommandId="CMD",IdempotencyKey="STEP:STEP:TRANSPORT",TaskId="TASK",StepId="STEP",VehicleId="AGV",VendorCommandId="STEP",CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,PayloadJson=payload,ExecutionRouteSnapshotJson=frozenJson,ExecutionRouteSnapshotHash=revision,ExecutionMapId="M",ExecutionMapVersionId="V1",SentAt=now.AddSeconds(-3).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    clock.UtcNow=now;
    runtime.Replace(new("M","V1",map,now)); source.OnRead=null;source.Hang=false;source.Evidence=new(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});
    envelope.Body=new("CALIBRATED","AGV","LocalCartesian",.5,true,true);gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;policy.Enabled=true;
}
var receipts=new TrafficSegmentReceiptService(db,inspector,gate,clock);
int ReceiptCount()=>db.Queryable<TrafficSegmentReceiptEntity>().Count();
int EventCount()=>db.Queryable<TrafficSegmentReceiptEventEntity>().Count();
int LeaseCount()=>db.Queryable<TrafficResourceLeaseEntity>().Count();
Reset();var issued=await receipts.IssueAsync("STEP",default);
Check(issued.State==TrafficSegmentReceiptState.Pending&&issued.ReceiptId is not null&&issued.CandidateCount==1&&
    issued.Candidates.Count==1&&issued.Progress is not null,"真实检查器生成后台凭据、候选与两帧观察摘要");
Check(!issued.ResourcesReleased&&LeaseCount()==1&&ReceiptCount()==1&&EventCount()==1,"签发只保存凭据与审计，不删租约");
var row=db.Queryable<TrafficSegmentReceiptEntity>().First();
Check(row.BindingHash==TransportRouteSnapshots.Hash(row.BindingJson)&&row.BindingJson.Contains("EPOCH")&&row.BindingJson.Contains("LEASE"),"凭据绑定完整证据与原租约及摘要");
Check(row.CommandId=="CMD"&&row.RouteRevision==revision&&row.StepId=="STEP"&&row.VehicleId=="AGV","凭据身份完全从后台记录构建");
Check(issued.ExpiresAt==now.AddSeconds(2),"有效期最长两秒且使用UTC");
Check((await receipts.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.Pending&&EventCount()==1,"只读检查凭据不写重复审计");
clock.UtcNow=now.AddSeconds(2);
Check((await receipts.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.Expired&&LeaseCount()==1,"过期边界拒绝凭据且保持占用");
Check((await receipts.ReadAsync("FAKE",default)).State==TrafficSegmentReceiptState.Unavailable,"客户端自造编号不可用");

Reset();issued=await receipts.IssueAsync("STEP",default);
var restarted=new TrafficSegmentReceiptService(db,inspector,gate,clock);
Check((await restarted.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.PreviousSession&&ReceiptCount()==1&&EventCount()==1,"重建服务实例未提交凭据失效但审计保留");
var guarded=false;try{db.Updateable<TrafficSegmentReceiptEntity>().Where(x=>true).SetColumns(x=>x.ExpiresAtUtc==now.AddHours(1).UtcDateTime).ExecuteCommand();}catch(Exception ex){guarded=ex.ToString().Contains("segment receipt is immutable");}
Check(guarded,"数据库禁止延长旧凭据有效期");
guarded=false;try{db.Updateable<TrafficSegmentReceiptEventEntity>().Where(x=>true).SetColumns(x=>x.Kind=="Committed").ExecuteCommand();}catch(Exception ex){guarded=ex.ToString().Contains("segment receipt event is immutable");}
Check(guarded,"签发审计不能改写成释放完成");

async Task Invalidated(string name,Action change)
{
    Reset();var receipt=await receipts.IssueAsync("STEP",default);Check(receipt.State==TrafficSegmentReceiptState.Pending,name+" 先签发");change();
    var view=await receipts.ReadAsync(receipt.ReceiptId!,default);
    Check(view.State==TrafficSegmentReceiptState.Invalidated&&LeaseCount()==1&&EventCount()==1,name+" 失效且不解锁");
}
await Invalidated("取消任务",()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.CancelRequestId=="CANCEL").ExecuteCommand());
await Invalidated("恢复屏障关闭",()=>barrier.IsOpen=false);
await Invalidated("地图切换",()=>runtime.Replace(new("M","V2",map,now)));
await Invalidated("原租约时期变化",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>true).SetColumns(x=>x.AcquiredAt==now.LocalDateTime).ExecuteCommand());
await Invalidated("评估配置变化",()=>policy.SafetyMarginMeters=3);policy.SafetyMarginMeters=.15;
Reset();issued=await receipts.IssueAsync("STEP",default);barrier.IsOpen=false;
Check((await receipts.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.Invalidated,"门禁关闭观察后凭据撤销");barrier.IsOpen=true;
Check((await receipts.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.Invalidated,"门禁恢复不能复活已撤销凭据");
Reset();issued=await receipts.IssueAsync("STEP",default);clock.UtcNow=now.AddMilliseconds(-500);
Check((await receipts.ReadAsync(issued.ReceiptId!,default)).State==TrafficSegmentReceiptState.Invalidated,"签发后时钟回退撤销凭据");
Reset();policy.Enabled=false;Check((await receipts.IssueAsync("STEP",default)).State==TrafficSegmentReceiptState.Unavailable&&ReceiptCount()==0,"生产默认关闭不签发凭据");
Reset();var noSource=new TrafficSegmentReceiptService(db,new TrafficSegmentReleaseInspector(db,runtime,gate,policy),gate,clock);
Check((await noSource.IssueAsync("STEP",default)).State==TrafficSegmentReceiptState.Unavailable&&ReceiptCount()==0,"未注册现场源不产生虚假凭据");
Reset();source.Evidence=source.Evidence! with{First=source.Evidence.First with{ObservedAt=now.AddSeconds(-4.5)},Second=source.Evidence.Second with{ObservedAt=now.AddSeconds(-4)}};
db.Updateable<AgvCommandOutboxEntity>().Where(x=>true).SetColumns(x=>x.SentAt==now.AddSeconds(-6).LocalDateTime).ExecuteCommand();
issued=await receipts.IssueAsync("STEP",default);Check(issued.ExpiresAt==now.AddSeconds(.5),"有效期受最旧原样本年龄约束");
Reset();db.Ado.ExecuteCommand("CREATE TRIGGER fail_receipt_event BEFORE INSERT ON traffic_segment_receipt_event BEGIN SELECT RAISE(ABORT,'simulated issuance audit failure'); END;");
Check((await receipts.IssueAsync("STEP",default)).State==TrafficSegmentReceiptState.Unavailable&&ReceiptCount()==0&&EventCount()==0&&LeaseCount()==1,"签发审计失败整笔回滚且占用不变");
db.Ado.ExecuteCommand("DROP TRIGGER fail_receipt_event");
Check((await receipts.IssueAsync("STEP",default)).State==TrafficSegmentReceiptState.Pending&&ReceiptCount()==1&&EventCount()==1,"签发事务回滚后可重新采集签发");
Reset();source.OnRead=()=>policy.MaximumAgeSeconds=6;
Check((await receipts.IssueAsync("STEP",default)).State==TrafficSegmentReceiptState.Unavailable&&ReceiptCount()==0,"采集中配置变化拒绝签发");policy.MaximumAgeSeconds=5;
Reset();using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();var propagated=false;try{await receipts.IssueAsync("STEP",cancelled.Token);}catch(OperationCanceledException){propagated=true;}Check(propagated&&ReceiptCount()==0,"调用方取消不留下半份凭据");}

File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"receipt-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="authoritative server receipt issuance/read, transactional issuance audit only; no release or commit API"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} receipt checks. Database: {root}");
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
