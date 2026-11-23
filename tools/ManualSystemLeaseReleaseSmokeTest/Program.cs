using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var root = Path.Combine(Path.GetTempPath(), "AGV-manual-system-lease-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "test.db") });
var catalog = new TrafficResourceCatalog([new("R-SYS", TrafficResourceType.NarrowAisle), new("R-SYS2", TrafficResourceType.NarrowAisle),
    new("R-SYS3", TrafficResourceType.NarrowAisle), new("R-SYS4", TrafficResourceType.NarrowAisle),
    new("R-ORPHAN", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(database, catalog).InitializeAsync(default);
var manager = new TrafficResourceManager(database, catalog, new(TimeSpan.FromMinutes(2)));
var now = DateTimeOffset.UtcNow;

AgvRobotSnapshot Robot(string id, double velocity, params SeerTaskSnapshot[] tasks) => new(id, id, "127.0.0.1", 19204,
    ConnectionState.Online, SignalQuality.Good, 5, 0, 0, .95, "C", "C", 80, false, false, "Idle", null, null, null,
    tasks, [], now, now, 0, null, null, 1)
{
    VelocityX = velocity, VelocityY = 0, AngularVelocity = 0
};
var fleet = new Vehicles(
    Robot("V-STOP", 0),
    Robot("V-MOVING", 0.5),
    Robot("V-BUSY", 0, new SeerTaskSnapshot("VT-1", 1, null)));
var service = new ManualReleaseWorkflowService(database, new(TimeSpan.FromMinutes(1)), fleet);
var user = TrafficUserContext.LocalOperation("system-test-operator");

async Task<ManualReleaseWorkflowResult> Release(string resource, string vehicle, string session)
{
    await manager.TryAcquireAsync(session + "-LOCK", session, vehicle, [resource], default);
    var lease = database.Queryable<TrafficResourceLeaseEntity>().Single(l => l.ResourceId == resource);
    return await service.SubmitAsync(user, new SubmitManualReleaseRequest(lease.LeaseId,
        "现场核对车辆已离开，人工解锁系统路权", true, "RELEASE " + resource), default);
}

// Session leases (charging, return to standby, commissioning) have no task row. They may be
// released only when the vehicle itself proves it is stopped, fresh and idle.
var accepted = await Release("R-SYS", "V-STOP", "SESSION-1");
Check(accepted.Accepted, "无任务记录的系统路权在车辆停稳、遥测新鲜且无活动任务时可以人工释放（R8）");
Check(!database.Queryable<TrafficResourceLeaseEntity>().Any(l => l.ResourceId == "R-SYS"),
    "人工释放后系统路权被删除");

var moving = await Release("R-SYS2", "V-MOVING", "SESSION-2");
Check(!moving.Accepted && moving.Message.Contains("运动"), "车辆仍在运动时拒绝释放系统路权");
var busy = await Release("R-SYS3", "V-BUSY", "SESSION-3");
Check(!busy.Accepted && busy.Message.Contains("活动任务"), "车辆仍有活动任务时拒绝释放系统路权");
var missing = await Release("R-SYS4", "V-UNKNOWN", "SESSION-4");
Check(!missing.Accepted && missing.Message.Contains("车辆状态缺失"), "车辆状态缺失时拒绝释放系统路权");
// A business-looking task with no record is not a system session: it stays refused as before.
await manager.TryAcquireAsync("STEP-ORPHAN", "TASK-ORPHAN", "V-STOP", ["R-ORPHAN"], default);
var orphan = await service.SubmitAsync(user, new SubmitManualReleaseRequest(
    database.Queryable<TrafficResourceLeaseEntity>().Single(l => l.ResourceId == "R-ORPHAN").LeaseId,
    "任务记录缺失的业务路权保持拒绝", true, "RELEASE R-ORPHAN"), default);
Check(!orphan.Accepted && orphan.Message.Contains("无法证明"), "无任务记录的业务路权即使车辆停稳也保持拒绝");
Check(new[] { "R-SYS2", "R-SYS3", "R-SYS4" }.All(id => database.Queryable<TrafficResourceLeaseEntity>().Any(l => l.ResourceId == id)),
    "被拒绝的系统路权申请不删除任何租约");

Console.WriteLine($"Manual system lease release: {checks} checks passed.");

sealed class Vehicles(params AgvRobotSnapshot[] snapshots) : IAgvStateCache
{
    public event Action<AgvRobotStateChanged>? StateChanged;
    public long Version => 1;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => snapshots;
    public bool TryGet(string vehicleId, out AgvRobotSnapshot value)
    {
        var match = snapshots.FirstOrDefault(s => s.VehicleId == vehicleId);
        value = match!;
        return match is not null;
    }
}
