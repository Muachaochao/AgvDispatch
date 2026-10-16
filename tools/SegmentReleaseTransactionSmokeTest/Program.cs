using System.Text.Json;
using SqlSugar;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(), "AGV-segment-transaction-" + Guid.NewGuid().ToString("N"));
var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(root,"test.db") });
db.CodeFirst.InitTables<DispatchTaskEntity,DispatchTaskStepEntity,AgvCommandOutboxEntity,TrafficRequestEntity,TrafficResourceLeaseEntity>();
db.CodeFirst.InitTables<SimRequest,SimAudit>();
db.Ado.ExecuteCommand("CREATE UNIQUE INDEX sim_audit_lease_epoch ON simulation_segment_audit(LeaseId,AcquiredAt)");
var map = new MapDefinition("test","","LocalCartesian","m",
    [new("A",0,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P1"},new("B",2,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P2"},new("C",4,0,MapNodeType.Station,true,[]){AgvSiteId="P3"}],
    [new("AB","A","B",2,1,true,true,["R","R2"]),new("BC","B","C",2,1,true,true,[])],
    [new("R",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)]),new("R2",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)])]);
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
    db.Deleteable<SimRequest>().ExecuteCommand();db.Deleteable<SimAudit>().ExecuteCommand();db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficRequestEntity>().ExecuteCommand();
    db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommand();db.Deleteable<DispatchTaskStepEntity>().ExecuteCommand();db.Deleteable<DispatchTaskEntity>().ExecuteCommand();
    db.Insertable(new DispatchTaskEntity { TaskId="TASK",StationId="ST",VehicleId="AGV",VendorTaskId="STEP",Stage=(int)DispatchTaskStage.GoingToPickup }).ExecuteCommand();
    db.Insertable(new DispatchTaskStepEntity { StepId="STEP",TaskId="TASK",StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Executing,VehicleId="AGV",VendorTaskId="STEP",SourceSite="P1",DestinationSite="P3" }).ExecuteCommand();
    db.Insertable(new AgvCommandOutboxEntity { CommandId="CMD",IdempotencyKey="STEP:STEP:TRANSPORT",TaskId="TASK",StepId="STEP",VehicleId="AGV",VendorCommandId="STEP",CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,PayloadJson=payload,ExecutionRouteSnapshotJson=frozenJson,ExecutionRouteSnapshotHash=revision,ExecutionMapId="M",ExecutionMapVersionId="V1",SentAt=now.AddSeconds(-3).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE2",ResourceId="R2",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    runtime.Replace(new("M","V1",map,now)); source.OnRead=null;source.Evidence=new(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});
    envelope.Body=new("CALIBRATED","AGV","LocalCartesian",.5,true,true);gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;policy.Enabled=true;
}
var transaction=new SimulationTransaction(db,runtime,gate,inspector,source,envelope,clock,policy);
int LeaseCount()=>db.Queryable<TrafficResourceLeaseEntity>().Count();
int AuditCount()=>db.Queryable<SimAudit>().Count();
int RequestCount()=>db.Queryable<SimRequest>().Count();
Reset();var plan=await transaction.PrepareAsync();Check(plan is not null,"两帧合格证据构建模拟提交计划");
var result=await transaction.CommitAsync(plan!,"REQ");
Check(result.Accepted&&!result.Replayed&&result.Count==2,"同一事务释放两条已完全通过的模拟租约");
Check(LeaseCount()==0&&AuditCount()==2&&RequestCount()==1,"租约删除和逐资源审计及请求头完整提交");
var audit=db.Queryable<SimAudit>().ToList();Check(audit.All(a=>a.RouteRevision==revision&&a.EvidenceJson.Contains("EPOCH")),"审计保存原路线摘要和原两帧证据");
var replay=await transaction.CommitAsync(plan!,"REQ");Check(replay.Accepted&&replay.Replayed&&replay.Count==2&&AuditCount()==2,"重复提交只返回原结果且不重复写审计");
db.Insertable(new TrafficResourceLeaseEntity{LeaseId="NEW-LEASE",ResourceId="R",OwnerStepId="OTHER",TaskId="OTHER",VehicleId="OTHER",MapId="M",MapVersionId="V1",AcquiredAt=now.LocalDateTime}).ExecuteCommand();
Check((await transaction.CommitAsync(plan!,"REQ")).Replayed&&LeaseCount()==1,"原请求重放不会删除后来取得资源的其他车辆");
Check(!(await transaction.CommitAsync("DIFFERENT","REQ")).Accepted&&LeaseCount()==1,"同请求号不可绑定不同计划");
Check(!(await transaction.CommitAsync(plan!,"REQ2")).Accepted&&LeaseCount()==1,"已消费计划不得换请求号再次作用");

async Task Deny(string name,Action change)
{
    Reset();transaction.BeforeCommit=null;transaction.AfterDelete=null;clock.UtcNow=now;
    var id=await transaction.PrepareAsync();Check(id is not null,name+" 先建立有效计划");change();
    var expected=LeaseCount();var outcome=await transaction.CommitAsync(id!,Guid.NewGuid().ToString("N"));
    Check(!outcome.Accepted&&LeaseCount()==expected&&AuditCount()==0&&RequestCount()==0,name+" 拒绝且保留原占用");
}
await Deny("评估后取消",()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.Stage==(int)DispatchTaskStage.CancellationPending).ExecuteCommand());
await Deny("评估后恢复门禁关闭",()=>barrier.IsOpen=false);
await Deny("评估后地图门禁关闭",()=>gate.SetMode(MapExecutionMode.Held));
await Deny("评估后活动地图替换",()=>runtime.Replace(new("M","V2",map,now)));
await Deny("原租约取得时刻变化",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>x.LeaseId=="LEASE").SetColumns(x=>x.AcquiredAt==now.LocalDateTime).ExecuteCommand());
await Deny("原租约更换车辆",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>x.LeaseId=="LEASE").SetColumns(x=>x.VehicleId=="OTHER").ExecuteCommand());
await Deny("路径观察更换",()=>db.Updateable<TrafficRequestEntity>().Where(x=>true).SetColumns(x=>x.EdgeIds=="[]").ExecuteCommand());
await Deny("最新证据离线",()=>source.Evidence=source.Evidence! with{Second=source.Evidence.Second with{Online=false}});
await Deny("包络标定失效",()=>envelope.Body=envelope.Body! with{CalibrationVerified=false});
await Deny("提交前证据过期",()=>clock.UtcNow=now.AddSeconds(10));
await Deny("提交前停用评估",()=>policy.Enabled=false);
clock.UtcNow=now;Reset();transaction.BeforeCommit=null;transaction.AfterDelete=null;
plan=await transaction.PrepareAsync();db.Updateable<TrafficResourceLeaseEntity>().Where(x=>true).SetColumns(x=>x.RenewedAt==now.LocalDateTime).ExecuteCommand();
Check((await transaction.CommitAsync(plan!,"HEARTBEAT")).Accepted,"正常心跳续期不改变原归属允许模拟提交");

Reset();plan=await transaction.PrepareAsync();
db.Ado.ExecuteCommand("CREATE TRIGGER sim_audit_failure BEFORE INSERT ON simulation_segment_audit WHEN NEW.ResourceId='R2' BEGIN SELECT RAISE(ABORT,'simulated audit failure'); END;");
result=await transaction.CommitAsync(plan!,"FAIL-AUDIT");
Check(!result.Accepted&&LeaseCount()==2&&AuditCount()==0&&RequestCount()==0,"第二条审计失败回滚已删租约及第一条审计");
db.Ado.ExecuteCommand("DROP TRIGGER sim_audit_failure");
Check((await transaction.CommitAsync(plan!,"FAIL-AUDIT")).Accepted&&AuditCount()==2,"回滚后原请求可完整重试不留半条结果");

Reset();plan=await transaction.PrepareAsync();using(var cancel=new CancellationTokenSource())
{
    transaction.AfterDelete=()=>cancel.Cancel();var propagated=false;
    try{await transaction.CommitAsync(plan!,"CANCEL-TX",cancel.Token);}catch(OperationCanceledException){propagated=true;}
    Check(propagated&&LeaseCount()==2&&AuditCount()==0&&RequestCount()==0,"删除后调用方取消仍回滚全部占用并传播取消");
}
transaction.AfterDelete=null;
Reset();clock.UtcNow=now;plan=await transaction.PrepareAsync();transaction.AfterDelete=()=>barrier.IsOpen=false;
Check(!(await transaction.CommitAsync(plan!,"BARRIER-DURING")).Accepted&&LeaseCount()==2&&AuditCount()==0,"事务内恢复屏障失效回滚全部删除与审计");transaction.AfterDelete=null;
Reset();plan=await transaction.PrepareAsync();transaction.AfterDelete=()=>clock.UtcNow=now.AddSeconds(10);
Check(!(await transaction.CommitAsync(plan!,"STALE-DURING")).Accepted&&LeaseCount()==2&&AuditCount()==0,"事务末尾证据过期回滚全部删除与审计");transaction.AfterDelete=null;clock.UtcNow=now;
Reset();plan=await transaction.PrepareAsync();transaction.AfterDelete=()=>policy.SafetyMarginMeters=3;
Check(!(await transaction.CommitAsync(plan!,"POLICY-DURING")).Accepted&&LeaseCount()==2&&AuditCount()==0,"事务内安全裕量改变回滚全部删除与审计");transaction.AfterDelete=null;policy.SafetyMarginMeters=.15;
Reset();plan=await transaction.PrepareAsync();transaction.BeforeCommit=()=>db.Updateable<DispatchTaskEntity>().Where(x=>true).SetColumns(x=>x.CancelRequestId=="LATE-CANCEL").ExecuteCommand();
Check(!(await transaction.CommitAsync(plan!,"RACE")).Accepted&&LeaseCount()==2,"获取控制门前的取消竞态拒绝");transaction.BeforeCommit=null;

Reset();plan=await transaction.PrepareAsync();Task<Outcome> one;Task<Outcome> two;
using(ExecutionContext.SuppressFlow())
{
    one=Task.Run(()=>transaction.CommitAsync(plan!,"CONCURRENT"));
    two=Task.Run(()=>transaction.CommitAsync(plan!,"CONCURRENT"));
}
var parallel=await Task.WhenAll(one,two);
Check(parallel.All(o=>o.Accepted)&&parallel.Count(o=>o.Replayed)==1&&LeaseCount()==0&&AuditCount()==2&&RequestCount()==1,"两个独立执行上下文并发提交一次释放一次重放");
var freshSimulator=new SimulationTransaction(db,runtime,gate,inspector,source,envelope,clock,policy);
Check((await freshSimulator.CommitAsync(plan!,"CONCURRENT")).Replayed&&AuditCount()==2,"重建模拟协调器依靠持久化请求重放不需内存计划");
Reset();source.Evidence=source.Evidence! with{Second=source.Evidence.Second with{Quality=SignalQuality.Bad}};
Check(await transaction.PrepareAsync() is null&&LeaseCount()==2,"没有合格证据不产生提交计划");

File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"transaction-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="test-only SQLite transaction prototype, no production release service or device connection"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} simulation transaction checks. Database: {root}");

sealed class Barrier:IRecoveryBarrier{public bool IsOpen{get;set;}=true;}
sealed class FixedClock(DateTimeOffset now):TimeProvider{public DateTimeOffset UtcNow=now;public override DateTimeOffset GetUtcNow()=>UtcNow;}
sealed class Source:IAuthoritativeTrafficProgressReader
{
    public TrafficProgressPair? Evidence;public TrafficProgressPair? LastRead;public Action? OnRead;
    public Task<TrafficProgressPair?> ReadAsync(string vehicleId,CancellationToken ct){OnRead?.Invoke();LastRead=Evidence;return Task.FromResult(Evidence);}
}
sealed class Envelope:ITrafficBodyEnvelopeProvider
{
    public TrafficBodyEnvelope? Body;public TrafficBodyEnvelope? LastRead;
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId,CancellationToken ct){LastRead=Body;return Task.FromResult(Body);}
}
