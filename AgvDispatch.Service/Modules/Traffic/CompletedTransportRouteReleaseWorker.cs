using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

/// <summary>
/// Persistent backstop for the narrow gap between step completion and the next
/// transport command. It never sends an AGV command or advances a PLC signal.
/// </summary>
public sealed class CompletedTransportRouteReleaseWorker(
    ILogger<CompletedTransportRouteReleaseWorker> logger,
    ISqlSugarClient database,
    CompletedTransportRouteReleaseService releaseService,
    IMapExecutionGate gate,
    TrafficSegmentReleasePolicy policy) : IsolatedBackgroundService
{
    private readonly Dictionary<string, string> _lastReason = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!policy.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var access = await gate.EnterAsync(
                    MapExecutionOperation.ExistingWork, stoppingToken);
                if (access is null) continue;
                var activeTaskIds = await database.Queryable<DispatchTaskEntity>()
                    .Where(task => task.Stage < (int)DispatchTaskStage.Completed)
                    .Select(task => task.TaskId).ToListAsync(stoppingToken);
                if (activeTaskIds.Count == 0) continue;
                // Filter by leases first: old completed steps without any lease must not
                // fill the bounded window and hide newly completed ones (R12).
                var leasedStepIds = (await database.Queryable<TrafficResourceLeaseEntity>()
                    .Select(lease => lease.OwnerStepId).Distinct().ToListAsync(stoppingToken)).ToArray();
                if (leasedStepIds.Length == 0) continue;
                var completedSteps = await database.Queryable<DispatchTaskStepEntity>()
                    .Where(step => activeTaskIds.Contains(step.TaskId) && leasedStepIds.Contains(step.StepId) &&
                        step.Status == (int)DispatchTaskStepStatus.Completed &&
                        (step.StepType == (int)DispatchTaskStepType.GoToPickup ||
                         step.StepType == (int)DispatchTaskStepType.DeliverFullBoxToDumper ||
                         step.StepType == (int)DispatchTaskStepType.EnterDumpingPosition ||
                         step.StepType == (int)DispatchTaskStepType.ReturnEmptyBox))
                    .OrderBy(step => step.UpdatedAt).Take(32).ToListAsync(stoppingToken);
                var current = completedSteps.Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
                foreach (var stepId in _lastReason.Keys.Where(id => !current.Contains(id)).ToArray()) _lastReason.Remove(stepId);
                foreach (var step in completedSteps)
                {
                    if (!await HasLeaseAsync(step.TaskId, step.StepId, stoppingToken)) continue;
                    var result = await releaseService.ReleaseAsync(
                        step.TaskId, step.StepId, stoppingToken);
                    if (result.ReleasedCount == 0)
                    {
                        var key = step.StepId + ":" + result.Reason;
                        if (!_lastReason.TryGetValue(step.StepId, out var previous) ||
                            previous != key)
                        {
                            _lastReason[step.StepId] = key;
                            logger.LogDebug(
                                "已完成运输步骤暂不释放前序路权。TaskId={TaskId}, StepId={StepId}, Reason={Reason}",
                                step.TaskId, step.StepId, result.Reason);
                        }
                        continue;
                    }
                    _lastReason.Remove(step.StepId);
                    logger.LogInformation(
                        "已完成运输步骤已释放前序路权。TaskId={TaskId}, StepId={StepId}, VehicleId={VehicleId}, Released={Released}, Reason={Reason}",
                        step.TaskId, step.StepId, step.VehicleId, result.ReleasedCount, result.Reason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "已完成运输路段释放检查失败；原路权保持不变。");
            }
        }
    }

    private Task<bool> HasLeaseAsync(string taskId, string stepId, CancellationToken ct) =>
        database.Queryable<TrafficResourceLeaseEntity>()
            .AnyAsync(lease => lease.TaskId == taskId && lease.OwnerStepId == stepId, ct);
}
