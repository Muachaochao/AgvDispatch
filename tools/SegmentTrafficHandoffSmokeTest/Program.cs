using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

var now = args.Length>2?DateTimeOffset.Parse(args[2],CultureInfo.InvariantCulture):DateTimeOffset.UtcNow;
var root = args.Length>1?args[1]:Path.Combine(Path.GetTempPath(), "AGV-segment-handoff-" + Guid.NewGuid().ToString("N"));
var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(root,"test.db") });
await new DatabaseInitializer(db).InitializeAsync(default);
var map = new MapDefinition("test","","LocalCartesian","m",
    [new("A",0,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P1"},new("B",2,0,MapNodeType.Waypoint,true,[]){AgvSiteId="P2"},new("C",4,0,MapNodeType.Station,true,["FINAL"]){AgvSiteId="P3"}],
    [new("AB","A","B",2,1,true,true,["R","R2"]),new("BC","B","C",2,1,true,true,[])],
    [new("R",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)]),new("R2",TrafficResourceType.NarrowAisle,1,[new(0,-1),new(1,-1),new(1,1),new(0,1)]),new("FINAL",TrafficResourceType.Station,1,[new(3.5,-1),new(4.5,-1),new(4.5,1),new(3.5,1)])]);
var runtime = new ActiveMapRuntime();
var barrier = new Barrier(); var gate = new MapExecutionGate(barrier); gate.SetMode(MapExecutionMode.Running);
var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload("STEP","AGV","P1","P3",["P1","P2","P3"],null),new JsonSerializerOptions(JsonSerializerDefaults.Web));
var fixtureRoute = new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\",\"R2\",\"FINAL\"]",MapId="M",MapVersionId="V1",State="Held" };
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
    db.Insertable(new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\",\"R2\",\"FINAL\"]",MapId="M",MapVersionId="V1",State="Held" }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE2",ResourceId="R2",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="FINAL-LEASE",ResourceId="FINAL",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    clock.UtcNow=now;
    runtime.Replace(new("M","V1",map,now)); source.OnRead=null;source.Hang=false;source.Evidence=new(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});
    envelope.Body=new("CALIBRATED","AGV","LocalCartesian",.5,true,true);gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;policy.Enabled=true;
}
var options=new TrafficSegmentCommitOptions{Enabled=true};
var service=new TrafficSegmentReceiptService(db,inspector,gate,clock,options);
var catalog=new TrafficResourceCatalog(map.Resources);
var manager=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)),clock);
var control=new TrafficResourceControlService(db,catalog,runtime,clock:clock);
async Task WaitFile(string file)
{
    var deadline=Stopwatch.StartNew();while(!File.Exists(file)){if(deadline.Elapsed>TimeSpan.FromSeconds(30))throw new TimeoutException(file);await Task.Delay(50);}
}
async Task<Process> Start(string mode,string? directory=null)
{
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(Program).Assembly.Location);
    var childRoot=directory??root;
    start.ArgumentList.Add(mode);start.ArgumentList.Add(childRoot);start.ArgumentList.Add(now.ToString("O"));
    var process=Process.Start(start)!;
    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));}
    catch{if(!process.HasExited)process.Kill(entireProcessTree:true);throw;}
    var output=await stdout;var error=await stderr;
    File.WriteAllText(Path.Combine(childRoot,mode.TrimStart('-')+".log"),output+error);
    if(process.ExitCode!=0)throw new Exception(output+error);
    return process;
}

if(args.Length==0)
{
    // Parent only coordinates this test's own child, then inspects the retained isolated database/report.
    var child=Start("--handoff");await WaitFile(Path.Combine(root,"ready.json"));
    var ready=JsonSerializer.Deserialize<Ready>(File.ReadAllText(Path.Combine(root,"ready.json")))!;
    Check(ready.Pid!=Environment.ProcessId,"实际独立模拟后台进程");
    Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==3,"父进程观察释放前原占用保持");
    File.WriteAllText(Path.Combine(root,"go.request"),"release");
    using(var completed=await child)Check(completed.HasExited,"模拟后台正常结束");
    var report=JsonSerializer.Deserialize<Report>(File.ReadAllText(Path.Combine(root,"handoff-result.json")))!;
    Check(report.Checks>=15,"独立后台完成资源交接断言");
    Check(db.Queryable<TrafficSegmentCommitEntity>().Count()==1&&db.Queryable<TrafficSegmentCommitLeaseEntity>().Count()==2,"父进程确认提交与审计持久化");
    using(var replay=await Start("--replay"))Check(replay.HasExited,"新独立进程完成历史结果重放");
    Check(db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId=="TASK-B").Count()==2,"跨进程重放保留后来车辆占用");
    Check(db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="NEXT").ResourceIds=="[\"FINAL\"]","父进程确认当前请求只显示剩余终点");
    var pendingRoot=Path.Combine(root,"pending-restart");
    using(var issuer=await Start("--pending",pendingRoot))Check(issuer.HasExited,"单独进程签发未提交凭据后正常退出");
    using(var reject=await Start("--pending-restart",pendingRoot))Check(reject.HasExited,"真正新进程拒绝未提交旧会话凭据");
    var pendingDb=DatabaseFactory.Create(new DatabaseOptions{DatabasePath=Path.Combine(pendingRoot,"test.db")});
    Check(pendingDb.Queryable<TrafficResourceLeaseEntity>().Count()==3&&pendingDb.Queryable<TrafficSegmentCommitEntity>().Count()==0,"未提交凭据跨进程不释放原占用");
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"handoff-result.json"),JsonSerializer.Serialize(new{Checks=checks+report.Checks+3,ParentChecks=checks,ChildChecks=report.Checks+3,Database=root,Scope="independent simulated backend and real traffic manager, private file coordination; no production IPC or devices"},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"PASS {checks+report.Checks+3} handoff checks. Database: {root}");return;
}
if(args[0]=="--pending")
{
    Reset();var receipt=await service.IssueAsync("STEP",default);
    Check(receipt.State==TrafficSegmentReceiptState.Pending,"独立进程保存未提交凭据");
    File.WriteAllText(Path.Combine(root,"pending-ready.json"),JsonSerializer.Serialize(new Ready(Environment.ProcessId,receipt.ReceiptId!,"UNCOMMITTED")));return;
}
if(args[0]=="--pending-restart")
{
    var old=JsonSerializer.Deserialize<Ready>(File.ReadAllText(Path.Combine(root,"pending-ready.json")))!;
    Check((await service.ReadAsync(old.ReceiptId,default)).State==TrafficSegmentReceiptState.PreviousSession&&
        !(await service.CommitAsync(new(old.ReceiptId,old.RequestId),default)).Accepted,"真正进程重启使未提交凭据失效且不能消费");return;
}
if(args[0]=="--replay")
{
    var ready=JsonSerializer.Deserialize<Ready>(File.ReadAllText(Path.Combine(root,"ready.json")))!;
    var closed=new TrafficSegmentReceiptService(db,inspector,gate,clock);
    var replay=await closed.CommitAsync(new(ready.ReceiptId,ready.RequestId),default);
    Check(replay.Accepted&&replay.Replayed&&!replay.ResourcesReleasedNow&&db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId=="TASK-B").Count()==2,"实际新进程默认关闭仅返回原提交结果");return;
}
Reset();
db.Insertable(new DispatchTaskEntity{TaskId="TASK-B",StationId="ST-B",VehicleId="AGV-B",Stage=(int)DispatchTaskStage.WaitingForVehicle}).ExecuteCommand();
db.Insertable(new DispatchTaskStepEntity{StepId="STEP-B",TaskId="TASK-B",Sequence=1,StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Ready,VehicleId="AGV-B"}).ExecuteCommand();
await manager.RecordRouteAsync(new("STEP-B","TASK-B","AGV-B","A","B",["AB"],["R","R2"],false,"共享前序路段申请"){MapId="M",MapVersionId="V1"},default);
Check(!(await manager.TryAcquireAsync("STEP-B","TASK-B","AGV-B",["R","R2"],default)).Acquired,"第二车等待原车共享路权");
var waiting=(await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-B");
Check(waiting.State=="Waiting"&&waiting.Blockers.Count==2&&waiting.Blockers.All(b=>b.TaskId=="TASK"),"等待原因准确关联原车两资源");
var issued=await service.IssueAsync("STEP",default);Check(issued.CandidateCount==2,"签发只包含可释放前序资源");
var readyData=new Ready(Environment.ProcessId,issued.ReceiptId!,"HANDOFF");
File.WriteAllText(Path.Combine(root,"ready.json"),JsonSerializer.Serialize(readyData));
await WaitFile(Path.Combine(root,"go.request"));
source.Evidence=new(first with{Sequence=12,ObservedAt=now.AddMilliseconds(-600)},first with{Sequence=13,ObservedAt=now.AddMilliseconds(-200)});
Check((await service.CommitAsync(new(readyData.ReceiptId,readyData.RequestId),default)).Accepted,"后台消费凭据与剩余展示同事务提交");
var requestRow=db.Queryable<TrafficRequestEntity>().Single(r=>r.StepId=="STEP");
Check(requestRow.State=="Held"&&requestRow.ResourceIds=="[\"FINAL\"]"&&requestRow.Reason.Contains("剩余"),"释放后立即显示剩余终点保持");
Check(requestRow.EdgeIds=="[\"AB\",\"BC\"]"&&db.Queryable<AgvCommandOutboxEntity>().Single().ExecutionRouteSnapshotHash==revision,"诊断更新不改原路线或发送快照");
waiting=(await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-B");
Check(waiting.State=="Waiting"&&waiting.Blockers.Count==0,"等待原因动态解除但查询不会自动授予");
Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId=="TASK-B"),"只读观察不制造第二车占用");
Check((await manager.TryAcquireAsync("STEP-B","TASK-B","AGV-B",["R","R2"],default)).Acquired,"实际申请后第二车取得释放资源");
var current=(await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP");
Check(current.ResourceIds.SequenceEqual(["FINAL"])&&current.Blockers.Count==0,"后来占用已过路段不误显示为原车阻塞");
Check(db.Queryable<TrafficLifecycleEventEntity>().Any(e=>e.StepId=="STEP-B"&&e.Kind=="BlockingCleared"),"第二车容量阻塞解除写入历史");
await manager.RenewTaskAsync("TASK",default);
Check(db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId=="TASK").Count()==1&&db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId=="TASK-B").Count()==2,"原车续期只保持终点不重获已释放路段");
Check((await service.CommitAsync(new(readyData.ReceiptId,readyData.RequestId),default)).Replayed&&db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId=="TASK-B").Count()==2,"重放不删除第二车新占用");
await manager.RecordRouteAsync(new("NEXT","TASK","AGV","C","C",[],["FINAL"],false,"同任务下一步正常交接"){MapId="M",MapVersionId="V1"},default);
Check((await manager.TryAcquireAsync("NEXT","TASK","AGV",["FINAL"],default)).Acquired,"同任务正常交接复用原资源容量");
await manager.ReleaseTaskExceptStepAsync("TASK","NEXT",default);
Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId=="STEP")&&db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId=="NEXT"&&l.ResourceId=="FINAL"),"正常交接释放前步骤仅保留新步骤占用");
db.Updateable<DispatchTaskEntity>().Where(t=>t.TaskId=="TASK").SetColumns(t=>t.Stage==(int)DispatchTaskStage.CancellationPending).ExecuteCommand();
await manager.RenewTaskAsync("TASK",default);
current=(await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="NEXT");
Check(current.State=="Retained"&&current.ResourceIds.SequenceEqual(["FINAL"]),"取消原任务仍保留剩余终点供现场核对");
Check(!(await manager.TryAcquireAsync("STEP-B","TASK-B","AGV-B",["FINAL"],default)).Acquired,"取消不开放原车终点给其他车辆");
File.WriteAllText(Path.Combine(root,"handoff-result.json"),JsonSerializer.Serialize(new Report(checks)));
Console.WriteLine($"Child handoff: {checks} checks");
sealed record Ready(int Pid,string ReceiptId,string RequestId);
sealed record Report(int Checks);
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
