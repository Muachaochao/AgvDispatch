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
await outbox.TryPrepareTransportStepAsync(queuedTask.Task.TaskId,queuedStep.StepId,"CAR-ONLINE",["A","B"],new("Wait"),default);
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
Console.WriteLine("Evidence: " + root);

sealed class Fleet : IAgvGateway
{
    public bool Offline = true;
    public List<string> Sends { get; } = [];
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("FLEET", "Synthetic", ConnectionState.Degraded, DateTimeOffset.Now, null));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([
        new("CAR-OFFLINE", Offline ? ConnectionState.Disconnected : ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now),
        new("CAR-ONLINE", ConnectionState.Online, "Idle", 80, "A", DateTimeOffset.Now)]);
    public Task<string> CreateTransportTaskAsync(string id, string? car, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    { Sends.Add(car!); return Task.FromResult(id); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task CancelTaskAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
