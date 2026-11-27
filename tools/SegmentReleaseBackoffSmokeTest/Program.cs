using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var root = Path.Combine(Path.GetTempPath(), "AGV-segment-backoff-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "test.db") });
await new DatabaseInitializer(database).InitializeAsync(default);
var now = DateTime.Now;
foreach (var stepId in new[] { "S-NO-LEASE", "S-SLOW", "S-FAST" })
{
    database.Insertable(new DispatchTaskStepEntity
    {
        StepId = stepId, TaskId = "T-" + stepId, Sequence = 1, StepType = (int)DispatchTaskStepType.GoToPickup,
        Status = (int)DispatchTaskStepStatus.Executing, VehicleId = "V-" + stepId,
        CreatedAt = now, UpdatedAt = stepId == "S-NO-LEASE" ? now.AddMinutes(-1) : now
    }).ExecuteCommand();
    if (stepId != "S-NO-LEASE")
        database.Insertable(new TrafficResourceLeaseEntity
        {
            LeaseId = "L-" + stepId, OwnerStepId = stepId, TaskId = "T-" + stepId,
            VehicleId = "V-" + stepId, ResourceId = "R-" + stepId,
            AcquiredAt = now, RenewedAt = now, ExpiresAt = now.AddMinutes(1)
        }).ExecuteCommand();
}

// S-SLOW never proves progress and spends 1.2 s per check, like a vehicle whose two frames never
// arrive within the wait budget. S-FAST is an ordinary vehicle that answers immediately.
var calls = new Dictionary<string, int>();
var receipts = new Receipts(async stepId =>
{
    lock (calls) calls[stepId] = calls.GetValueOrDefault(stepId) + 1;
    if (stepId == "S-SLOW") await Task.Delay(1200);
});
var options = new TrafficSegmentReleaseWorkerOptions
    { Enabled = true, PollIntervalMs = 250, FrameWaitSeconds = 4, MaximumStepsPerCycle = 2 };
var clock = new JumpingClock();
var worker = new RouteSegmentPassThroughReleaseWorker(NullLogger<RouteSegmentPassThroughReleaseWorker>.Instance,
    database, receipts, new Commits(), new TrafficSegmentReleaseMonitor(), options,
    new TrafficSegmentReleasePolicy { Enabled = true }, new TrafficSegmentCommitOptions { Enabled = true }, clock);

await worker.StartAsync(CancellationToken.None);
await Task.Delay(2000);
clock.JumpForward();
await Task.Delay(1500);
await worker.StopAsync(CancellationToken.None);

var fast = calls.GetValueOrDefault("S-FAST");
var slow = calls.GetValueOrDefault("S-SLOW");
Console.WriteLine($"  calls: fast={fast} slow={slow}");
Check(fast >= 6, "一台车取不到两帧进度时，其他车仍按轮询周期立即检查（R9）");
Check(slow == 1, "取不到两帧进度的车退避，系统时钟前跳不会提前结束退避");
Check(!calls.ContainsKey("S-NO-LEASE"), "无租约步骤不会占用逐段释放批次额度（R12）");
Console.WriteLine($"Segment release backoff: {checks} checks passed.");

sealed class Receipts(Func<string, Task> onIssue) : ITrafficSegmentReceiptService
{
    public async Task<TrafficSegmentReceiptView> IssueAsync(string stepId, CancellationToken ct)
    {
        await onIssue(stepId);
        return new(null, TrafficSegmentReceiptState.Unavailable, "两帧进度未取得", null, 0);
    }
    public Task<TrafficSegmentReceiptView> ReadAsync(string receiptId, CancellationToken ct) => throw new NotSupportedException();
}

sealed class Commits : ITrafficSegmentCommitService
{
    public Task<TrafficSegmentCommitResult> CommitAsync(TrafficSegmentCommitRequest request, CancellationToken ct) => throw new NotSupportedException();
    public Task<TrafficSegmentCommitResult?> ReadCommittedAsync(TrafficSegmentCommitRequest request, CancellationToken ct) => throw new NotSupportedException();
}

sealed class JumpingClock : TimeProvider
{
    private int _offsetHours;
    public void JumpForward() => Interlocked.Exchange(ref _offsetHours, 2);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(Volatile.Read(ref _offsetHours));
}
