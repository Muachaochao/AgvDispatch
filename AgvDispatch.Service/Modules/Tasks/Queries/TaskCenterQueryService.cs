using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class TaskCenterQueryService(ITaskCenterReadRepository repository,
    IPlcSignalCache signals, DispatchTaskCreationOptions options, TaskParameterRuntime? parameters = null, TimeProvider? clock = null, HandshakeExecutionOptions? handshakeOptions = null) : ITaskCenterQueryService
{
    public Task<TaskListPage> QueryAsync(TaskListQuery query, CancellationToken cancellationToken) =>
        repository.QueryAsync(query, cancellationToken);

    public Task<TaskHistoryPage> QueryHistoryAsync(TaskHistoryQuery query, CancellationToken cancellationToken) => repository.QueryHistoryAsync(query, cancellationToken);

    public Task<TaskCenterDetail?> GetDetailAsync(string taskId, CancellationToken cancellationToken) => GetDetailAsync(new TaskDetailRequest(taskId), cancellationToken);
    public async Task<TaskCenterDetail?> GetDetailAsync(TaskDetailRequest request, CancellationToken cancellationToken)
    {
        var detail = await repository.GetDetailAsync(request, cancellationToken);
        if (detail is null) return null;
        var task = detail.Task;
        if (task.Stage is DispatchTaskStage.Completed or DispatchTaskStage.Cancelled or DispatchTaskStage.Faulted)
            return EnrichSteps(detail with { WaitingExplanation = task.Stage == DispatchTaskStage.Faulted
                ? "任务故障：查看任务、步骤和命令错误。路权不会因故障自动释放。"
                : "任务已结束；若仍有路权，请按人工处置流程核查。" }, null);
        if (task.Stage is DispatchTaskStage.CancellationPending or DispatchTaskStage.CancellationReview)
            return EnrichSteps(detail with { WaitingExplanation = task.ErrorMessage ?? "取消处置中；后续步骤保持冻结，原路权保留。" }, null);
        var current = detail.Steps.FirstOrDefault(s => s.Status != DispatchTaskStepStatus.Completed);
        var conditions = new List<TaskPlcCondition>();
        void Add(string station, string variable, bool expected)
        {
            signals.TryGet(station, variable, out var signal);
            conditions.Add(TaskSignalPredicate.Evaluate(station, variable, expected, signal));
        }
        var explanation = "当前步骤等待执行；请结合步骤状态和命令队列排查。";
        if (task.Stage == DispatchTaskStage.Created)
        {
            Add(task.StationId, "FullBoxRequestAcknowledged", true);
            explanation = "等待满箱呼叫应答写入并校验，成功后进入待派车。";
        }
        else if (task.Stage == DispatchTaskStage.WaitingForVehicle)
            explanation = "等待可用车辆、工位映射、可行路径和交通资源；此处不代表已诊断出具体阻塞原因。";
        else if (current?.Status == DispatchTaskStepStatus.Ready)
        {
            conditions.AddRange(TaskStepProgressRules.Observe(current.StepType, task.StationId, options.DumperStationId, signals));
            switch (current.StepType)
            {
                case DispatchTaskStepType.PickupFullBox:
                    explanation = "取箱握手：先确认箱体离开，再写入并回读取箱通知/返箱责任；具体保持及清位以本任务契约为准。";
                    break;
                case DispatchTaskStepType.WaitForDumperReady:
                    explanation = "等待倒料机无故障、等待位握手回读和Ready。";
                    break;
                case DispatchTaskStepType.DumpFullBox:
                    explanation = "倒料握手：等待倒料机完成并允许离开。";
                    break;
                case DispatchTaskStepType.PlaceEmptyBox:
                    explanation = "放箱握手：等待空箱放到位回读和箱体检测。";
                    break;
                case DispatchTaskStepType.CompletePlcHandshake:
                    explanation = "PLC收尾：按本任务契约逐阶段核对返箱、通知复位和责任闭合，显示当前回读及目标。";
                    break;
                default:
                    explanation = "等待运输步骤准备、站点映射、路径和路权；查看命令队列及错误说明。";
                    break;
            }
        }
        else if (current is not null)
        {
            var state = current.Status switch
            {
                DispatchTaskStepStatus.Pending => "未激活",
                DispatchTaskStepStatus.CommandPending => "命令待发送",
                DispatchTaskStepStatus.Sending => "发送中",
                DispatchTaskStepStatus.Executing => "执行中",
                DispatchTaskStepStatus.RetryScheduled => "等待重试",
                DispatchTaskStepStatus.Failed => "失败",
                DispatchTaskStepStatus.Cancelled => "已取消",
                DispatchTaskStepStatus.ManualIntervention => "人工处理",
                _ => current.Status.ToString()
            };
            explanation = $"当前步骤状态：{state}；PLC握手尚未进入，查看厂家任务编号和命令发送状态。";
        }
        var checkpoint=detail.Handshakes.FirstOrDefault(h=>h.StepId==current?.StepId);
        if(checkpoint!=null && current is not null)
        {
            conditions=checkpoint.ContractVersion==HandshakeExecutionOptions.Version
                ?PersistedHandshakeConditions.Observe(checkpoint,current.StepType,task.StationId,options.DumperStationId,signals,(clock??TimeProvider.System).GetUtcNow(),handshakeOptions?.StateMaxAgeSeconds??5).ToList()
                :[];
            explanation=$"持久化阶段：{checkpoint.Phase}；周期{checkpoint.CycleId}；修订{checkpoint.Revision}。{checkpoint.Reason}";
            if(checkpoint.PendingSignal!=null)explanation+=" 原写入结果待人工核对，不自动重发。";
            if(handshakeOptions?.Enabled!=true)explanation+=" 新契约执行未启用，检查点仅供核对。";
        }
        if(detail.Handshakes.Count>0&&(handshakeOptions?.Enabled!=true||detail.Handshakes.Any(h=>h.ContractVersion!=handshakeOptions.PlcContractVersion)))
            explanation="原任务契约与当前执行模式/版本不一致，后续PLC与原Outbox保持核对，不降级发送。"+explanation;
        var unmet = conditions.Where(condition => !condition.Satisfied).Select(condition =>
            $"{condition.StationId}/{condition.VariableName} 期望={condition.ExpectedValue}，当前={condition.CurrentValue?.ToString() ?? "未知"}，质量={condition.Quality}").ToArray();
        if (unmet.Length > 0) explanation += " 未满足：" + string.Join("；", unmet) + "。";
        if (!string.IsNullOrWhiteSpace(current?.ErrorMessage)) explanation += " 最近步骤错误：" + current.ErrorMessage;
        var retry = detail.Commands.FirstOrDefault(command => command.StepId == current?.StepId && command.Status == AgvCommandStatus.RetryScheduled);
        if (retry is not null) explanation += $" 命令已尝试 {retry.AttemptCount} 次，下次重试：{retry.NextAttemptAt:MM-dd HH:mm:ss}。";
        return EnrichSteps(detail with { PlcConditions = conditions, WaitingExplanation = explanation }, current?.StepId);
    }

    private TaskCenterDetail EnrichSteps(TaskCenterDetail detail, string? currentStepId)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var thresholds = parameters?.StepTimeouts.ToDictionary(row => row.StepType, row => row.Seconds);
        var terminal = detail.Task.Stage is DispatchTaskStage.Completed or DispatchTaskStage.Cancelled or DispatchTaskStage.Faulted or DispatchTaskStage.CancellationReview;
        var steps = detail.Steps.Select(step =>
        {
            var elapsed = step.Status == DispatchTaskStepStatus.Pending ? "未开始" : "未记录开始时间";
            var duration = TimeSpan.Zero;
            if (step.StartedAt is { } started)
            {
                var end = step.CompletedAt ?? (terminal ? detail.Task.UpdatedAt : now);
                duration = end < started ? TimeSpan.Zero : end - started;
                elapsed = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
                if (step.CompletedAt is null && terminal) elapsed += "（截至任务结束）";
            }
            var threshold = thresholds?.GetValueOrDefault(step.StepType) ?? 0;
            var timedOut = !terminal && step.StepId == currentStepId && step.StartedAt is not null &&
                step.CompletedAt is null && threshold > 0 && duration.TotalSeconds >= threshold;
            var timeoutText = threshold == 0 ? "超时提示：关闭" : timedOut
                ? $"已超时 · 阈值 {threshold} 秒 · 超出 {(long)(duration.TotalSeconds - threshold)} 秒"
                : $"超时阈值：{threshold} 秒";
            return step with { TimeoutSeconds = threshold, IsTimedOut = timedOut, TimeoutText = timeoutText, IsCurrent = step.StepId == currentStepId, ElapsedText = elapsed,
                WaitingExplanation = step.StepId == currentStepId ? detail.WaitingExplanation : "" };
        }).ToArray();
        return AddDiagnostics(detail with { Steps = steps, DiagnosticsObservedAt = now, HandshakeMode = handshakeOptions?.Enabled==true?"PersistentExecution":"ObservationOnly", HandshakeContractVersion = handshakeOptions?.PlcContractVersion??"" });
    }
}
