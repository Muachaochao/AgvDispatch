using System.Text.Json;
using System.IO;
using System.Diagnostics;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(), "AGV-partial-recovery-" + Guid.NewGuid().ToString("N"));
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
    db.Insertable(new DispatchTaskStepEntity { StepId="STEP",TaskId="TASK",Sequence=1,StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Executing,VehicleId="AGV",VendorTaskId="STEP",SourceSite="P1",DestinationSite="P3" }).ExecuteCommand();
    db.Insertable(new AgvCommandOutboxEntity { CommandId="CMD",IdempotencyKey="STEP:STEP:TRANSPORT",TaskId="TASK",StepId="STEP",VehicleId="AGV",VendorCommandId="STEP",CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,PayloadJson=payload,ExecutionRouteSnapshotJson=frozenJson,ExecutionRouteSnapshotHash=revision,ExecutionMapId="M",ExecutionMapVersionId="V1",SentAt=now.AddSeconds(-3).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficRequestEntity { StepId="STEP",TaskId="TASK",VehicleId="AGV",Source="A",Destination="C",EdgeIds="[\"AB\",\"BC\"]",ResourceIds="[\"R\"]",MapId="M",MapVersionId="V1",State="Held" }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEASE2",ResourceId="R2",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    db.Insertable(new TrafficResourceLeaseEntity { LeaseId="FINAL-LEASE",ResourceId="FINAL",OwnerStepId="STEP",TaskId="TASK",VehicleId="AGV",MapId="M",MapVersionId="V1",AcquiredAt=now.AddSeconds(-5).LocalDateTime,ExpiresAt=now.AddSeconds(-1).LocalDateTime }).ExecuteCommand();
    clock.UtcNow=now;
    runtime.Replace(new("M","V1",map,now)); source.OnRead=null;source.Hang=false;source.Evidence=new(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});
    envelope.Body=new("CALIBRATED","AGV","LocalCartesian",.5,true,true);gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;policy.Enabled=true;
}
Reset();
var receipts=new TrafficSegmentReceiptService(db,inspector,gate,clock,new(){Enabled=true});
var issued=await receipts.IssueAsync("STEP",default);
source.Evidence=new(first with{Sequence=12,ObservedAt=now.AddMilliseconds(-600)},first with{Sequence=13,ObservedAt=now.AddMilliseconds(-200)});
Check((await receipts.CommitAsync(new(issued.ReceiptId!,"PARTIAL"),default)).Accepted,"消费组件生成真实分段释放审计与剩余租约");
Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==1&&db.Queryable<TrafficSegmentCommitLeaseEntity>().Count()==2,"原车只剩终点且两资源历史可追溯");
var originalCommand=db.Queryable<AgvCommandOutboxEntity>().Single().CommandId;
var originalLeases=db.Queryable<TrafficResourceLeaseEntity>().ToList().Select(l=>l.LeaseId).ToArray();
var vehicles=new SwitchVehicleCache();var signals=new SwitchSignalCache();var vendor=new Gateway();
var recoveryBarrier=new RecoveryBarrier();var recoveryGate=new MapExecutionGate(recoveryBarrier);recoveryGate.SetMode(MapExecutionMode.Running);
var recovery=new ExecutionRecoveryService(db,recoveryBarrier,recoveryGate,vendor,vehicles,signals,new(),runtime);
var admin=new TrafficUserContext("partial-test\\engineer","工程师",TrafficPermission.View|TrafficPermission.RecoverExecution);
await recovery.InitializeAsync(default);
void Fresh()=>vehicles.Items.Add(SwitchVehicleCache.Ready("AGV") with{X=3,Y=0,CurrentStation="P2",LastSuccessfulPollAt=DateTimeOffset.Now,UpdatedAt=DateTimeOffset.Now,Tasks=[new("STEP",2,null)]});
Fresh();await recovery.InspectAsync(default);
var status=await recovery.GetAsync(default);var item=status.Items.Single(i=>i.TaskId=="TASK");
Check(!status.Released&&!recoveryBarrier.IsOpen,"分段释放后的原运输启动时仍冻结");
Check(item.ResourceSummary.Contains("已按证据释放")&&item.ResourceSummary.Contains("R2")&&item.ResourceSummary.Contains("FINAL"),"恢复页面区分历史已释放与当前剩余终点");
Check(item.Evidence.Contains("SegmentReleases")&&item.Evidence.Contains("EvidenceHash")&&item.Evidence.Contains("EPOCH"),"恢复证据保留原分段审计及控制器周期关联");
var decision=new RecoveryDecisionRequest(status.SessionId,item.TicketId,item.Revision,RecoveryDecision.ContinueOriginal,"空载现场核对","已确认当前位置载荷归属以及原PLC周期用于模拟测试",true,true,true);
var denied=await recovery.DecideAsync(admin,decision,default);
Check(!denied.Accepted&&denied.Message.Contains("逐段释放"),"普通在线定位和Running原编号仍不能绕过分段续接限制");
Check(db.Queryable<AgvCommandOutboxEntity>().Single().CommandId==originalCommand&&db.Queryable<TrafficResourceLeaseEntity>().Single().ResourceId=="FINAL","拒绝不重派原命令或释放剩余占用");
// Simulate a stale/bypassed verification row to exercise the independent final release guard.
db.Updateable<ExecutionRecoveryTicketEntity>().Where(t=>t.TaskId=="TASK").SetColumns(t=>t.Verified==true).ExecuteCommand();
Check(!(await recovery.ReleaseAsync(admin,new(status.SessionId,"模拟核验旧确认不能绕过分段释放限制"),default)).Accepted&&!recoveryBarrier.IsOpen,"最终放行再次拒绝已有分段释放的执行节点");
Check(vendor.Sends==0&&vendor.Cancels==0,"恢复核验不发送或取消任何车辆动作");
db.Insertable(new DispatchTaskStepEntity{StepId="NEXT",TaskId="TASK",Sequence=2,StepType=(int)DispatchTaskStepType.PickupFullBox,
    Status=(int)DispatchTaskStepStatus.Pending,SourceSite="P3",VehicleId="AGV"}).ExecuteCommand();
vendor.State=AgvTransportTaskState.Completed;vehicles.Items.Clear();
vehicles.Items.Add(SwitchVehicleCache.Ready("AGV") with{X=4,Y=0,CurrentStation="P3",LastSuccessfulPollAt=DateTimeOffset.Now,
    UpdatedAt=DateTimeOffset.Now,Tasks=[new("STEP",4,null)]});
await recovery.InspectAsync(default);status=await recovery.GetAsync(default);item=status.Items.Single(i=>i.TaskId=="TASK");
var completion=await recovery.DecideAsync(admin,decision with{SessionId=status.SessionId,TicketId=item.TicketId,Revision=item.Revision,Decision=RecoveryDecision.RecordPhysicalCompletion},default);
Check(completion.Accepted&&db.Queryable<DispatchTaskStepEntity>().Single(s=>s.StepId=="STEP").Status==(int)DispatchTaskStepStatus.Completed,"原有到站停止与原任务完成条件满足仍可补记");
Check(!recoveryBarrier.IsOpen&&db.Queryable<TrafficResourceLeaseEntity>().Single().ResourceId=="FINAL","补记不自动放行PLC下一步或释放剩余占用");
db.Updateable<DispatchTaskEntity>().Where(t=>t.TaskId=="TASK").SetColumns(t=>new DispatchTaskEntity{
    Stage=(int)DispatchTaskStage.CancellationReview,CancelRequestId="CANCEL-SIM",CancelStepId="STEP",
    CancelVendorId="STEP",CancelVehicleId="AGV",CancelConfirmedAt=DateTime.Now}).ExecuteCommand();
await recovery.InspectAsync(default);status=await recovery.GetAsync(default);item=status.Items.Single(i=>i.TaskId=="TASK");
var close=await recovery.DecideAsync(admin,decision with{SessionId=status.SessionId,TicketId=item.TicketId,
    Revision=item.Revision,Decision=RecoveryDecision.CloseCancelled},default);
Check(!close.Accepted&&close.Message.Contains("路权仍占用")&&db.Queryable<TrafficResourceLeaseEntity>().Single().ResourceId=="FINAL","取消确认后关闭仍不自动释放部分运输剩余路权");
Check(db.Queryable<TrafficSegmentCommitLeaseEntity>().Count()==2&&db.Queryable<AgvCommandOutboxEntity>().Single().CommandId==originalCommand,"补记和取消核对保持已释放审计及原命令编号");

// Boot the real service twice from a private deployment; AGV/PLC and writers disabled.
var deployment=Path.Combine(root,"service");Directory.CreateDirectory(deployment);
var serviceSource=Path.GetFullPath("AgvDispatch.Service/bin/Debug/net10.0-windows10.0.19041.0/win-x64");
foreach(var file in Directory.EnumerateFiles(serviceSource,"*",SearchOption.AllDirectories))
{
    var relative=Path.GetRelativePath(serviceSource,file);if(relative.StartsWith("data"+Path.DirectorySeparatorChar)||relative.StartsWith("logs"+Path.DirectorySeparatorChar))continue;
    var target=Path.Combine(deployment,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
}
var pipe="PartialRecovery-"+Guid.NewGuid().ToString("N");
File.WriteAllText(Path.Combine(deployment,"appsettings.json"),JsonSerializer.Serialize(new{
    Database=new{Path=Path.Combine(root,"test.db")},Security=new{PipeName=pipe},Agv=new{Enabled=false,AllowCommands=false,Robots=Array.Empty<object>()},
    Plc=new{Enabled=false,AllowWrites=false,SignalCsvPath="config/plc-signals.csv"},Map=new{Enabled=false},
    TaskCreation=new{Enabled=false},Dispatching=new{Enabled=false},AgvCommandOutbox=new{Enabled=false},DispatchTaskSteps=new{Enabled=false},
    ExecutionIdentityAcquisition=new{Enabled=false},RecoveryIdentity=new{RequireMachineIdentity=false}},new JsonSerializerOptions{WriteIndented=true}));
string? priorSession=null;
for(var boot=1;boot<=2;boot++)
{
    var start=new ProcessStartInfo(Path.Combine(deployment,"AgvDispatch.Service.exe")){WorkingDirectory=deployment,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var arg in new[]{"--Agv:Enabled=false","--Agv:AllowCommands=false","--Plc:Enabled=false","--Plc:AllowWrites=false",
        "--Map:Enabled=false","--TaskCreation:Enabled=false","--Dispatching:Enabled=false","--AgvCommandOutbox:Enabled=false",
        "--DispatchTaskSteps:Enabled=false","--ExecutionIdentityAcquisition:Enabled=false","--RecoveryIdentity:RequireMachineIdentity=false"})start.ArgumentList.Add(arg);
    start.Environment["AGV_DISPATCH_DATABASE_PATH"]=Path.Combine(root,"test.db");start.Environment["AGV_DISPATCH_PIPE_NAME"]=pipe;
    using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    try
    {
        using var client=new NamedPipeTrafficControlClient(pipe);RecoveryStatus? observed=null;var timer=Stopwatch.StartNew();
        while(timer.Elapsed<TimeSpan.FromSeconds(25))
        {
            if(process.HasExited)throw new Exception("private service exited");
            try{observed=await client.GetExecutionRecoveryAsync(default);if(observed.Items.Any(i=>i.TaskId=="TASK"))break;}catch(IOException){}catch(TimeoutException){}
            await Task.Delay(150);
        }
        if(observed is null||!observed.Items.Any(i=>i.TaskId=="TASK"))throw new TimeoutException("private service readiness");
        var taskItem=observed.Items.Single(i=>i.TaskId=="TASK");
        var system=await client.GetSystemStatusAsync(default);
        Check(!system.Safety.AgvCommunicationEnabled&&!system.Safety.AgvCommandsAllowed&&!system.Safety.PlcCommunicationEnabled&&!system.Safety.PlcWritesAllowed,"实际后台采集和设备写入均关闭 boot"+boot);
        Check(process.Id!=Environment.ProcessId&&!observed.Released,"真实后台独立进程保持恢复屏障 boot"+boot);
        Check(taskItem.ResourceSummary.Contains("已按证据释放")&&taskItem.ResourceSummary.Contains("FINAL"),"真实后台IPC展示已释放及剩余占用 boot"+boot);
        Check(db.Queryable<TrafficResourceLeaseEntity>().ToList().Select(l=>l.LeaseId).SequenceEqual(originalLeases)&&db.Queryable<TrafficSegmentCommitLeaseEntity>().Count()==2,"真实启动保留原租约及分段审计 boot"+boot);
        Check(!(await client.ReleaseExecutionRecoveryAsync(new(observed.SessionId,"模拟现场材料不足必须拒绝服务放行"),default)).Accepted,"真实IPC拒绝未核对恢复放行 boot"+boot);
        if(priorSession is not null)Check(priorSession!=observed.SessionId,"真正服务进程重启创建新恢复批次");priorSession=observed.SessionId;
        File.WriteAllText(Path.Combine(root,"boot"+boot+".json"),JsonSerializer.Serialize(observed,new JsonSerializerOptions{WriteIndented=true}));
    }
    finally
    {
        if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();
        File.WriteAllText(Path.Combine(root,"service-boot"+boot+".log"),(await stdout)+(await stderr));
    }
}
Check(db.Queryable<AgvCommandOutboxEntity>().Single().CommandId==originalCommand&&db.Queryable<TrafficResourceLeaseEntity>().Single().ResourceId=="FINAL","两次真实服务重启不重建命令不回收剩余占用");
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"partial-recovery-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="real partial commit, simulated ordinary recovery evidence and two private actual Service.exe boots; no hardware"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} partial recovery checks. Database: {root}");
sealed class Gateway:IAgvGateway
{
    public int Sends,Cancels;
    public AgvTransportTaskState State=AgvTransportTaskState.GoingToDestination;
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>Task.FromResult(new DeviceConnectionStatus("SIM","SIM",ConnectionState.Online,DateTimeOffset.Now));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([]);
    public Task<string> CreateTransportTaskAsync(string business,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct){Sends++;return Task.FromResult(business);}
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>Task.FromResult<AgvTransportTaskStatus?>(new(id,id,State,"AGV",DateTimeOffset.Now));
    public Task CancelTaskAsync(string id,CancellationToken ct){Cancels++;return Task.CompletedTask;}
}
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
