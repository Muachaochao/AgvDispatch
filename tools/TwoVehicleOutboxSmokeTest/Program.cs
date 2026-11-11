using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.Combine(Path.GetTempPath(), "agv-two-outbox-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "private.db") });
await new DatabaseInitializer(db, new TrafficResourceCatalog([])).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db);
var outbox = new AgvCommandOutboxRepository(db);
var ids = new List<string>();
foreach (var car in new[] { "CAR-OFFLINE", "CAR-ONLINE" })
{
    var created = await tasks.TryCreateAsync(car + "-ORIGIN", "DUMPER", 0, default);
    await tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
    var step = (await tasks.GetStepsAsync(created.Task.TaskId, default))[0];
    if (!await outbox.TryPrepareTransportStepAsync(created.Task.TaskId, step.StepId, car, ["A", "B"], new("Wait"), default))
        throw new Exception("Could not prepare isolated command");
    ids.Add(step.StepId);
}
var gateway = new Fleet();
var gate = new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
using var sender = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance, new(), outbox, gateway, gate);
var method = typeof(AgvCommandOutboxDispatcher).GetMethod("DispatchDueCommandsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
async Task Tick() => await (Task)method.Invoke(sender, [CancellationToken.None])!;
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
for (var i = 0; i < 8; i++) await Tick();
var held = db.Queryable<AgvCommandOutboxEntity>().Where(c => c.StepId == ids[0]).Single();
var online = db.Queryable<AgvCommandOutboxEntity>().Where(c => c.StepId == ids[1]).Single();
Check(held.AttemptCount == 0 && held.Status == (int)AgvCommandStatus.Pending, "离线车八轮轮询不领取原命令、不消耗发送次数");
Check(online.AttemptCount == 1 && online.Status == (int)AgvCommandStatus.Sent && gateway.Sends.SequenceEqual(["CAR-ONLINE"]), "车队降级不阻止健康车首次发送");
gateway.Offline = false;
await Tick(); await Tick();
var resumed = db.Queryable<AgvCommandOutboxEntity>().Where(c => c.StepId == ids[0]).Single();
Check(resumed.CommandId == held.CommandId && resumed.StepId == held.StepId && resumed.AttemptCount == 1 && resumed.Status == (int)AgvCommandStatus.Sent,
    "离线车恢复后沿用原命令及原步骤，第一次发送仍计一次");
Check(gateway.Sends.Count == 2 && gateway.Sends.Distinct().Count() == 2, "两车各发送一次，不重复派发");
var roadMap = new MapDefinition("isolated road", "synthetic", "RobotWorld", "m",
    [new("A",0,0,MapNodeType.Waypoint,true,[],"A"),new("B",1,0,MapNodeType.Waypoint,true,[],"B")],
    [new("AB","A","B",1,1,true,true,[])],[]);
var runtime = new ActiveMapRuntime(); runtime.Replace(new("ROAD-MAP","V1",roadMap,DateTimeOffset.UtcNow));
var roads = new RoadClosureService(db,runtime,gate); await roads.InitializeAsync(default);
var roadUser = TrafficUserContext.LocalOperation("isolated-outbox");
var sealedRoad = await roads.CreateAsync(roadUser,new("outbox-seal","ROAD-MAP","V1",EngineeringRuntimeIdentity.MapHash(roadMap),
    "AB","A","B",true,"synthetic obstacle"),default);
var queuedTask = await tasks.TryCreateAsync("ROAD-CLOSED-ORIGIN","DUMPER",0,default);
await tasks.MarkAcknowledgedAsync(queuedTask.Task.TaskId,default);
var queuedStep=(await tasks.GetStepsAsync(queuedTask.Task.TaskId,default))[0];
Check(await outbox.TryPrepareTransportStepAsync(queuedTask.Task.TaskId,queuedStep.StepId,"CAR-ROAD",["A","B"],new("Wait"),default),
    "封路用例使用独立车辆登记原命令，不抢占前两辆仍有业务责任的车辆");
using var roadSender = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,new(),outbox,gateway,gate,roadClosures:roads);
for(var i=0;i<8;i++)await (Task)method.Invoke(roadSender,[CancellationToken.None])!;
var roadHeld = db.Queryable<AgvCommandOutboxEntity>().Single(c=>c.StepId==queuedStep.StepId);
Check(roadHeld.Status==(int)AgvCommandStatus.Pending&&roadHeld.AttemptCount==0&&gateway.Sends.Count==2,
    "已排队旧路线遇到封锁八轮不领取、不消耗尝试、不发送");
await roads.ReleaseAsync(roadUser,new("outbox-clear",sealedRoad.Closure!.ClosureId,sealedRoad.Revision,"synthetic clear",true),default);
await (Task)method.Invoke(roadSender,[CancellationToken.None])!;
var roadResumed = db.Queryable<AgvCommandOutboxEntity>().Single(c=>c.StepId==queuedStep.StepId);
Check(roadResumed.CommandId==roadHeld.CommandId&&roadResumed.Status==(int)AgvCommandStatus.Sent&&roadResumed.AttemptCount==1&&gateway.Sends.Count==3,
    "人工解封后同一Outbox首次发送一次，未重建业务步骤");
// Isolated downstream tickets are committed together with the first command.
using(var admissionDb=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"admission.db")}))
{
 await new DatabaseInitializer(admissionDb,new TrafficResourceCatalog([])).InitializeAsync(default);
 var repo=new DispatchTaskRepository(admissionDb);
 var policy=new DownstreamAdmissionOptions{Enabled=true,Capacity=1};
 var guarded=new AgvCommandOutboxRepository(admissionDb,admission:policy);
 async Task<(string Task,string Step)> Create(string station){var created=await repo.TryCreateAsync(station,"DUMPER",0,default);await repo.MarkAcknowledgedAsync(created.Task.TaskId,default);return(created.Task.TaskId,(await repo.GetStepsAsync(created.Task.TaskId,default))[0].StepId);}
 var first=await Create("FIRST");var next=await Create("NEXT");
 Check(await guarded.TryPrepareTransportStepAsync(first.Task,first.Step,"C1",["A","B"],null,default),"首个取箱命令与下游准入票在同一事务提交");
 Check(!(await guarded.TryPrepareTransportStepAsync(next.Task,next.Step,"C2",["A","B"],null,default))&&
       admissionDb.Queryable<AgvCommandOutboxEntity>().Count()==1&&
       admissionDb.Queryable<DispatchTaskEntity>().Single(t=>t.TaskId==next.Task).VehicleId==null,"准入已满时不抢车辆、不建命令、不提前取箱");
 guarded=new AgvCommandOutboxRepository(admissionDb,admission:policy);
 Check(!(await guarded.GetPickupAdmissionAsync(next.Task,default)).Allowed,"重建仓储后准入票从原持久化责任恢复");
 foreach(var stage in new[]{DispatchTaskStage.Faulted,DispatchTaskStage.CancellationPending,DispatchTaskStage.CancellationReview}){
  await admissionDb.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)stage).Where(t=>t.TaskId==first.Task).ExecuteCommandAsync();
  Check(!(await guarded.GetPickupAdmissionAsync(next.Task,default)).Allowed,$"{stage}仍保留准入票，不因故障自动多取箱");
 }
 await admissionDb.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.Completed).Where(t=>t.TaskId==first.Task).ExecuteCommandAsync();
 await admissionDb.Insertable(new TrafficResourceLeaseEntity{LeaseId="retained",ResourceId="BAY",TaskId=first.Task,
  OwnerStepId=first.Step,VehicleId="C1",AcquiredAt=DateTime.Now,RenewedAt=DateTime.Now,ExpiresAt=DateTime.Now.AddMinutes(-1)}).ExecuteCommandAsync();
 foreach(var closed in new[]{DispatchTaskStage.Completed,DispatchTaskStage.Cancelled}){
  await admissionDb.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)closed).Where(t=>t.TaskId==first.Task).ExecuteCommandAsync();
  Check(!(await guarded.GetPickupAdmissionAsync(next.Task,default)).Allowed,$"{closed}但路权尚未核验释放时仍保留准入票，过期不自动腾位");
 }
 await admissionDb.Deleteable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId=="retained").ExecuteCommandAsync();
 Check(await guarded.TryPrepareTransportStepAsync(next.Task,next.Step,"C2",["A","B"],null,default),"原业务完成后下一单可取得准入票；全过程未修改九步或PLC电平");
 await admissionDb.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.Completed).Where(t=>t.TaskId==next.Task).ExecuteCommandAsync();
 var contenders=new[]{await Create("RACE-A"),await Create("RACE-B")};
 var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var arrivals=0;
 var grants=await Task.WhenAll(contenders.Select((entry,index)=>Task.Run(async()=>{
  using var connection=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"admission.db")});
  var contender=new AgvCommandOutboxRepository(connection,admission:policy);
  if(Interlocked.Increment(ref arrivals)==2)ready.SetResult();await ready.Task;
  try{return await contender.TryPrepareTransportStepAsync(entry.Task,entry.Step,"RACE-C"+index,["A","B"],null,default);}
  catch(InvalidOperationException error)when(error.InnerException?.Message.Contains("locked",StringComparison.OrdinalIgnoreCase)==true){return false;}
 })));
 Check(grants.Count(g=>g)==1&&admissionDb.Queryable<DispatchTaskEntity>().Count(t=>t.Stage==(int)DispatchTaskStage.SubmittingToAgv)==1,
  "两个独立数据库连接并发取箱只提交一张准入票，不超卖容量1");
}
Console.WriteLine("Evidence: " + root);

sealed class Fleet : IAgvGateway
{
    public bool Offline = true;
    public List<string> Sends { get; } = [];
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("FLEET", "Synthetic", ConnectionState.Degraded, DateTimeOffset.Now, null));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([
        new("CAR-OFFLINE", Offline ? ConnectionState.Disconnected : ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now),
        new("CAR-ONLINE", ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now),
        new("CAR-ROAD", ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now)]);
    public Task<string> CreateTransportTaskAsync(string id, string? car, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    { Sends.Add(car!); return Task.FromResult(id); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task CancelTaskAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
