using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

/// <summary>
/// Each cycle obtains authoritative two-frame progress, issues a short-lived
/// receipt for fully passed resources, and consumes it through the atomic
/// per-lease commit service. A resource is released only after the original
/// route's last use, the whole vehicle envelope, map/lease identity, and a
/// newer second evidence pass all agree. Future or re-entered resources stay
/// held.
/// </summary>
public sealed class RouteSegmentPassThroughReleaseWorker(
    ILogger<RouteSegmentPassThroughReleaseWorker> logger,
    ISqlSugarClient database,
    ITrafficSegmentReceiptService receipts,
    ITrafficSegmentCommitService commits,
    TrafficSegmentReleaseMonitor monitor,
    TrafficSegmentReleaseWorkerOptions worker,
    TrafficSegmentReleasePolicy policy,
    TrafficSegmentCommitOptions commit,
    TimeProvider? clock = null,
    DispatchTrafficWakeup? dispatchWakeup = null) : IsolatedBackgroundService
{
    private readonly Dictionary<string, string> _lastReason = new(StringComparer.Ordinal);
    // A step whose two-frame progress was not proven waits a frame budget before the next
    // attempt, so one stuck vehicle cannot delay every other vehicle's release on each poll.
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!worker.Enabled || !policy.Enabled || !commit.Enabled) return;
        worker.Validate();
        var interval = clock is SimulationTimeProvider simulation
            ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(worker.PollIntervalMs))
            : TimeSpan.FromMilliseconds(worker.PollIntervalMs);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "逐资源通过即释放周期失败；本轮不删除任何原路权。");
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var steps = await database.Queryable<DispatchTaskStepEntity>()
            .Where(step => step.Status == (int)DispatchTaskStepStatus.Executing &&
                (step.StepType == (int)DispatchTaskStepType.GoToPickup ||
                 step.StepType == (int)DispatchTaskStepType.DeliverFullBoxToDumper ||
                 step.StepType == (int)DispatchTaskStepType.EnterDumpingPosition ||
                 step.StepType == (int)DispatchTaskStepType.ReturnEmptyBox))
            .OrderBy(step => step.UpdatedAt)
            .Take(worker.MaximumStepsPerCycle)
            .ToListAsync(ct);
        // Forget bookkeeping of steps that have left the running set.
        var running = steps.Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
        foreach (var stepId in _retryAfter.Keys.Where(id => !running.Contains(id)).ToArray()) _retryAfter.Remove(stepId);
        foreach (var stepId in _lastReason.Keys.Where(id => !running.Contains(id)).ToArray()) _lastReason.Remove(stepId);
        var clockNow = clock ?? TimeProvider.System;
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (_retryAfter.TryGetValue(step.StepId, out var retryAt) && clockNow.GetUtcNow() < retryAt) continue;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var receipt = await receipts.IssueAsync(step.StepId, ct);
            if (receipt.State != TrafficSegmentReceiptState.Pending || string.IsNullOrWhiteSpace(receipt.ReceiptId))
            {
                // Only a check that actually waited for frames is backed off; an idle step is re-read at once.
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
                    _retryAfter[step.StepId] = clockNow.GetUtcNow() + TimeSpan.FromSeconds(worker.FrameWaitSeconds);
                Record(step, "Held", receipt.Reason, receipt.Candidates, receipt.Progress);
                continue;
            }
            _retryAfter.Remove(step.StepId);
            Record(step, "PassedPendingRelease",
                $"两帧进度与完整车体已证明 {receipt.CandidateCount} 项资源通过；正在原子提交。",
                receipt.Candidates, receipt.Progress);

            var result = await commits.CommitAsync(
                new(receipt.ReceiptId, "SEG-" + Guid.NewGuid().ToString("N")), ct);
            if (!result.Accepted)
            {
                Record(step, "Held", result.Reason, receipt.Candidates, receipt.Progress);
                continue;
            }

            var released = result.ReleasedLeaseIds.Count;
            if (released > 0) dispatchWakeup?.Publish();
            Record(step, "Released",
                released > 0
                    ? $"已原子释放 {released} 项完全通过且后续不再使用的资源；其余资源继续占用。"
                    : "凭据已提交，但本轮没有新增可释放资源。",
                receipt.Candidates, receipt.Progress);
            logger.LogInformation(
                "通过即释放已提交。TaskId={TaskId}, StepId={StepId}, VehicleId={VehicleId}, ReleasedLeaseIds={LeaseIds}",
                step.TaskId, step.StepId, step.VehicleId, string.Join(',', result.ReleasedLeaseIds));
        }
    }

    private void Record(DispatchTaskStepEntity step, string state, string reason,
        IReadOnlyList<TrafficSegmentReleaseCandidate> candidates,
        TrafficSegmentProgressSummary? progress)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        monitor.Update(new(step.TaskId, step.StepId, step.VehicleId ?? "", state, reason, now, candidates, progress));
        var key = step.StepId + ":" + state + ":" + reason;
        if (_lastReason.TryGetValue(step.StepId, out var previous) && previous == key) return;
        _lastReason[step.StepId] = key;
        logger.LogInformation(
            "通过即释放观察。TaskId={TaskId}, StepId={StepId}, VehicleId={VehicleId}, State={State}, Reason={Reason}, Verified={Verified}",
            step.TaskId, step.StepId, step.VehicleId ?? "", state, reason,
            string.Join(',', candidates.Where(candidate => candidate.EvidenceVerified)
                .Select(candidate => candidate.ResourceId)));
    }
}
