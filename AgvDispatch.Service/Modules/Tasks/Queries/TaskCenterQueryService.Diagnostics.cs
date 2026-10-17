using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class TaskCenterQueryService
{
    private static TaskCenterDetail AddDiagnostics(TaskCenterDetail detail)
    {
        var milestones = new List<TaskExecutionEvent>
        {
            new("task-created:" + detail.Task.TaskId, detail.Task.CreatedAt, "", "TaskCreated", "业务任务已创建", "数据库创建时间；不代表已派车。", "任务数据库")
        };
        foreach (var step in detail.Steps)
        {
            if (step.StartedAt is { } started) milestones.Add(new("step-started:" + step.StepId, started, step.StepId, "StepStarted", $"第{step.Sequence}步开始", "数据库步骤开始时间，不等于机械动作开始。", "步骤数据库"));
            if (step.CompletedAt is { } ended) milestones.Add(new("step-ended:" + step.StepId, ended, step.StepId, "StepCompleted", $"第{step.Sequence}步记录结束", $"后台步骤结果：{step.Status}；不补造传感器或机械证据。", "步骤数据库"));
        }
        foreach (var command in detail.Commands.Where(c => c.SentAt != null))
            milestones.Add(new("command-sent:" + command.CommandId, command.SentAt!.Value, command.StepId, "CommandSent", "车端命令已发送并返回身份", $"命令{command.CommandId}；不等于车辆已运动或到位。", "命令数据库"));
        var timeline = detail.ExecutionTimeline.Concat(milestones).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id, StringComparer.Ordinal).TakeLast(100).ToArray();
        return detail with { ExecutionTimeline = timeline, ExecutionTimelineTotal = detail.ExecutionTimelineTotal + milestones.Count, Steps = detail.Steps.Select(step =>
        {
            var conditions = step.IsCurrent ? detail.PlcConditions : [];
            var command = detail.Commands.LastOrDefault(c => c.StepId == step.StepId);
            var commandText = command?.Status switch
            {
                AgvCommandStatus.Pending => "命令已登记，尚未确认车端接受。",
                AgvCommandStatus.Sending => "发送中；不能据此认定车辆已接受或已经运动。",
                AgvCommandStatus.Sent => "适配器已返回厂家任务身份；运行/成功另看原步骤结果，不证明物理到位。",
                AgvCommandStatus.RetryScheduled => "发送结果需核对，按原身份查询/重试；不代表机械动作失败。",
                AgvCommandStatus.Failed => "命令失败；核对原命令及现场结果。",
                AgvCommandStatus.Cancelled => "命令已取消；仍需核对现场和业务任务。",
                _ => "无独立车端命令记录。"
            };
            var unmet = conditions.FirstOrDefault(c => !c.Satisfied);
            var phase = step.IsCurrent && detail.Task.Stage == DispatchTaskStage.Created ? "等待满箱应答写入核对"
                : step.IsCurrent && detail.Task.Stage == DispatchTaskStage.WaitingForVehicle ? "等待派车核验；缺少细项证据时不猜测阻塞者"
                : step.Status switch
            {
                DispatchTaskStepStatus.Completed => "步骤已完成（后台持久化结果）",
                DispatchTaskStepStatus.Pending => "未激活；不使用当前PLC值判定未来/历史步骤",
                DispatchTaskStepStatus.CommandPending => "命令已登记，等待发送",
                DispatchTaskStepStatus.Sending => "命令发送/接受结果核对中",
                DispatchTaskStepStatus.Executing => "追踪原车端任务，尚未确认本步骤完成",
                DispatchTaskStepStatus.RetryScheduled => "原命令等待核对重试",
                DispatchTaskStepStatus.Failed => "步骤失败，查看原错误与处置记录",
                DispatchTaskStepStatus.Cancelled => "步骤已取消",
                DispatchTaskStepStatus.ManualIntervention => "等待人工核对",
                _ when !step.IsCurrent => "任务已结束或处置中；不以实时信号重判",
                _ when detail.Task.Stage is DispatchTaskStage.Created => "等待满箱应答写入核对",
                _ when detail.Task.Stage is DispatchTaskStage.WaitingForVehicle => "等待派车核验；缺少细项证据时不猜测阻塞者",
                _ when step.StepType == DispatchTaskStepType.CompletePlcHandshake => "执行收尾复位；列表显示目标，不是新增完成门禁",
                _ when unmet != null => $"当前观察：{unmet.Name} {unmet.Result}；{unmet.Reason}",
                _ when conditions.Count > 0 => "当前观察值满足列出条件；等待执行器完成写入/状态转换",
                _ => "等待运输准备；查看路径、资源与步骤错误"
            };
            var persisted=detail.Handshakes.FirstOrDefault(h=>h.StepId==step.StepId);
            var newContract = persisted!=null || detail.HandshakeMode=="PersistentExecution" || detail.Handshakes.Count>0;
            if(persisted!=null)phase=$"持久化阶段 {persisted.Phase} / r{persisted.Revision}：{persisted.Reason}";
            return step with
            {
                ProgressConditions = conditions, ObservedPhase = phase,
                CompletionRequirement = !newContract?TaskStepProgressRules.Requirement(step.StepType):"原运输按厂家任务终态核对；PLC阶段按新契约持久化顺序执行，本轮Done基线、Ready消费、保守返程到位及收尾回读分别核对。未来步骤尚无检查点时不能据当前电平认定完成。",
                EvidenceBoundary = !newContract?TaskStepProgressRules.Boundary(step.StepType):"软件规划契约，需要匹配PLC程序；写调用/回读不等于机械完成。待确认写入须通过恢复核对，不自动重发。",
                CommandStateExplanation = commandText
            };
        }).ToArray() };
    }
}
