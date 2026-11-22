using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>Layer four is the only planning-stage writer of resource requests. It grants no vehicle command.</summary>
public sealed class DispatchTrafficReservationService(ITrafficResourceManager resources, IDispatchPipelineMonitor? monitor = null,
    IDispatchOrchestrationRepository? tasks = null, DispatchOptimizationService? optimization = null, TimeProvider? clock = null)
{
    public Task PrepareCycleAsync(CancellationToken ct) => resources.PrepareDispatchCycleAsync(ct);
    public async Task<IReadOnlyDictionary<string, DispatchAssignmentTaskScore>> PrioritiesAsync(CancellationToken ct)
    {
        if (tasks is null) return new Dictionary<string, DispatchAssignmentTaskScore>(StringComparer.OrdinalIgnoreCase);
        var rows = (await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct))
            .Concat(await tasks.GetAssignedTasksAsync(ct)).Concat(await tasks.GetSubmittingTasksAsync(ct));
        var policy = optimization?.Options ?? new DispatchAssignmentOptions();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return rows.DistinctBy(t => t.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t.TaskId, t => DispatchTaskPriority.Score(t, policy, now), StringComparer.OrdinalIgnoreCase);
    }

    public async Task ObserveAsync(TrafficRouteObservation observation, CancellationToken ct) =>
        await resources.RecordRouteAsync(WithPriority(observation, await PrioritiesAsync(ct)), ct);

    // Withdrawing an ungranted recommendation never releases physical ownership.
    public Task SuspendRecommendationAsync(TrafficRouteObservation observation, CancellationToken ct) =>
        resources.RecordRouteAsync(observation with { QueuePriority = null, QueueEligible = false, Waiting = true }, ct);

    private static TrafficRouteObservation WithPriority(TrafficRouteObservation observation,
        IReadOnlyDictionary<string, DispatchAssignmentTaskScore> priorities) =>
        priorities.TryGetValue(observation.TaskId, out var score)
            ? observation with { QueuePriority = score.EffectivePriority } : observation;

    public async Task ObserveWaitingAsync(IReadOnlyList<TrafficRouteObservation> proposals, CancellationToken ct)
    {
        if (proposals.Count == 0) return;
        var priorities = await PrioritiesAsync(ct);
        foreach (var observation in proposals)
            if (observation.QueueEligible) await resources.RecordRouteAsync(WithPriority(observation, priorities), ct);
            else await SuspendRecommendationAsync(observation, ct);
        var queued = proposals.Count(observation => observation.QueueEligible);
        monitor?.ReportLayer("traffic", "Waiting", $"首窗排队 {queued} 项，参考路线 {proposals.Count - queued} 项。", queued);
    }

    public async Task<TrafficReservationResult> AcquireAsync(TrafficRouteObservation observation, CancellationToken ct)
    {
        monitor?.ReportLayer("traffic", "Running", "逐项核对路径资源容量与公平队列；已有路权保持不变。");
        await ObserveAsync(observation, ct);
        var result = await resources.TryAcquireAsync(observation.StepId, observation.TaskId, observation.VehicleId, observation.ResourceIds, ct);
        monitor?.ReportLayer("traffic", result.Acquired ? "Ready" : "Waiting", result.Acquired
            ? "本候选已取得当前窗口所需路权，交由安全层复核。"
            : $"等待公共资源 {result.BlockingResourceId}，原任务留在队列。", observation.ResourceIds.Count);
        return result;
    }

    public async Task<TrafficReservationResult> AcquireWaitingBayAsync(TrafficRouteObservation observation,
        TaskWaitingBayBinding binding, ITaskWaitingBayAllocator allocator, CancellationToken ct)
    {
        await ObserveAsync(observation, ct);
        return await allocator.TryAcquireAsync(binding, observation.ResourceIds, ct);
    }

    public Task RollbackUncommittedAsync(string stepId, CancellationToken ct) => resources.ReleaseStepAsync(stepId, ct);
    public Task HandoffAsync(string taskId, string stepId, CancellationToken ct) => resources.ReleaseTaskExceptStepAsync(taskId, stepId, ct);
}
