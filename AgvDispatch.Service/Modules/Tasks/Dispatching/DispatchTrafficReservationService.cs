using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>Layer four is the only planning-stage writer of resource requests. It grants no vehicle command.</summary>
public sealed class DispatchTrafficReservationService(ITrafficResourceManager resources, IDispatchPipelineMonitor? monitor = null)
{
    public Task PrepareCycleAsync(CancellationToken ct) => resources.PrepareDispatchCycleAsync(ct);
    public async Task ObserveWaitingAsync(IReadOnlyList<TrafficRouteObservation> proposals, CancellationToken ct)
    {
        foreach (var observation in proposals) await resources.RecordRouteAsync(observation, ct);
        if (proposals.Count > 0) monitor?.ReportLayer("traffic", "Waiting", "必要公共资源尚未释放，保留优先级与原始呼叫顺序。", proposals.Count);
    }

    public async Task<TrafficReservationResult> AcquireAsync(TrafficRouteObservation observation, CancellationToken ct)
    {
        monitor?.ReportLayer("traffic", "Running", "逐项核对路径资源容量与公平队列；已有路权保持不变。");
        await resources.RecordRouteAsync(observation, ct);
        var result = await resources.TryAcquireAsync(observation.StepId, observation.TaskId, observation.VehicleId, observation.ResourceIds, ct);
        monitor?.ReportLayer("traffic", result.Acquired ? "Ready" : "Waiting", result.Acquired
            ? "本候选已取得整条路径所需路权，交由安全层复核。"
            : $"等待公共资源 {result.BlockingResourceId}，原任务留在队列。", observation.ResourceIds.Count);
        return result;
    }

    public Task RollbackUncommittedAsync(string stepId, CancellationToken ct) => resources.ReleaseStepAsync(stepId, ct);
    public Task HandoffAsync(string taskId, string stepId, CancellationToken ct) => resources.ReleaseTaskExceptStepAsync(taskId, stepId, ct);
}
