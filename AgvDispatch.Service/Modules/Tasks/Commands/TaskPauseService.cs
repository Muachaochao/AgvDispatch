using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

public sealed class TaskPauseService(ISqlSugarClient database) : ITaskPauseService
{
    public async Task<TaskPauseResult> PauseAsync(
        TrafficUserContext user,
        TaskPauseRequest request,
        CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.RecoverExecution))
            return new(false, "暂停任务需要恢复处置权限。");
        var reason = request.Reason?.Trim() ?? "";
        if (!Guid.TryParseExact(request.RequestId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 64 ||
            reason.Length is < 10 or > 1000)
            return new(false, "暂停请求编号、任务编号或至少10字的暂停依据无效。");

        TaskPauseResult result = new(false, "任务未暂停。");
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var task = await database.Queryable<DispatchTaskEntity>()
                .Where(item => item.TaskId == request.TaskId).FirstAsync(ct);
            if (task is null)
            {
                result = new(false, "任务不存在。");
                return;
            }
            if (task.PauseRequestId == request.RequestId && task.PauseResumedAt is null &&
                task.Stage == (int)DispatchTaskStage.Faulted)
            {
                result = new(true, "该暂停请求已经生效，未重复处理。");
                return;
            }
            if (task.CancelRequestId is not null)
            {
                result = new(false, "任务已进入取消或恢复处置，禁止改用暂停。");
                return;
            }
            if (task.Stage != (int)request.ExpectedStage)
            {
                result = new(false, "任务阶段已变化，请刷新后重新核对。");
                return;
            }
            if ((int)request.ExpectedStage is < 0 or > 80)
            {
                result = new(false, "当前任务阶段不允许暂停。");
                return;
            }
            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item => item.TaskId == task.TaskId &&
                               item.Status == (int)DispatchTaskStepStatus.Ready)
                .OrderBy(item => item.Sequence).FirstAsync(ct);
            if (step is null)
            {
                result = new(false, "当前没有可安全暂停的就绪步骤；执行中、发送中或结果未知的动作只能走取消/恢复核对。");
                return;
            }

            var now = DateTime.Now;
            task.PauseRequestId = request.RequestId;
            task.PauseOriginalStage = task.Stage;
            task.PauseStepId = step.StepId;
            task.PauseReason = reason;
            task.PauseRequestedBy = user.WindowsIdentity;
            task.PauseRequestedAt = now;
            task.PauseResumeRequestId = null;
            task.PauseResumeReason = null;
            task.PauseResumeRequestedBy = null;
            task.PauseResumedAt = null;
            task.Stage = (int)DispatchTaskStage.Faulted;
            task.ErrorMessage = "任务已暂停：" + reason;
            task.UpdatedAt = now;
            step.Status = (int)DispatchTaskStepStatus.ManualIntervention;
            step.ErrorMessage = task.ErrorMessage;
            step.UpdatedAt = now;
            await database.Updateable(task).ExecuteCommandAsync(ct);
            await database.Updateable(step).ExecuteCommandAsync(ct);
            await database.Insertable(new TaskRuntimeEventEntity
            {
                TaskId = task.TaskId,
                StepId = step.StepId,
                Kind = "TaskPaused",
                Phase = "Frozen",
                StationId = task.StationId,
                RecordedAtUtc = now,
                Reason = reason,
                EvidenceJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    request.RequestId,
                    OriginalStage = (DispatchTaskStage)(task.PauseOriginalStage ?? task.Stage),
                    OriginalStepId = step.StepId,
                    user.WindowsIdentity
                })
            }).ExecuteCommandAsync(ct);
            result = new(true, "任务已暂停并冻结自动推进；原路权、设备和命令保持不变。");
        });
        if (!transaction.IsSuccess)
            throw new InvalidOperationException("暂停任务事务失败。", transaction.ErrorException);
        return result;
    }

    public async Task<TaskPauseResult> ResumeAsync(
        TrafficUserContext user,
        TaskPauseResumeRequest request,
        CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.RecoverExecution))
            return new(false, "恢复已暂停任务需要恢复处置权限。");
        var reason = request.Reason?.Trim() ?? "";
        if (!Guid.TryParseExact(request.RequestId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 64 ||
            reason.Length is < 10 or > 1000)
            return new(false, "恢复请求编号、任务编号或至少10字的恢复依据无效。");

        TaskPauseResult result = new(false, "任务未恢复。");
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var task = await database.Queryable<DispatchTaskEntity>()
                .Where(item => item.TaskId == request.TaskId).FirstAsync(ct);
            if (task is null)
            {
                result = new(false, "任务不存在。");
                return;
            }
            if (task.PauseResumeRequestId == request.RequestId && task.PauseResumedAt is not null)
            {
                result = new(true, "该恢复请求已经生效，未重复处理。");
                return;
            }
            if (task.PauseRequestId is null || task.PauseOriginalStage is null ||
                string.IsNullOrWhiteSpace(task.PauseStepId))
            {
                result = new(false, "任务没有可恢复的暂停记录。");
                return;
            }
            if (task.CancelRequestId is not null)
            {
                result = new(false, "任务已进入取消或恢复处置，不能直接恢复暂停。");
                return;
            }
            if (task.Stage != (int)DispatchTaskStage.Faulted)
            {
                result = new(false, "任务已不在暂停冻结状态。");
                return;
            }
            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item => item.TaskId == task.TaskId && item.StepId == task.PauseStepId)
                .FirstAsync(ct);
            if (step is null || step.Status != (int)DispatchTaskStepStatus.ManualIntervention)
            {
                result = new(false, "暂停步骤状态已变化，不能直接恢复。");
                return;
            }

            var now = DateTime.Now;
            task.Stage = task.PauseOriginalStage.Value;
            task.ErrorMessage = null;
            task.PauseResumeRequestId = request.RequestId;
            task.PauseResumeReason = reason;
            task.PauseResumeRequestedBy = user.WindowsIdentity;
            task.PauseResumedAt = now;
            task.UpdatedAt = now;
            step.Status = (int)DispatchTaskStepStatus.Ready;
            step.StartedAt = now;
            step.ErrorMessage = null;
            step.UpdatedAt = now;
            await database.Updateable(task).ExecuteCommandAsync(ct);
            await database.Updateable(step).ExecuteCommandAsync(ct);
            await database.Insertable(new TaskRuntimeEventEntity
            {
                TaskId = task.TaskId,
                StepId = step.StepId,
                Kind = "TaskResumed",
                Phase = "Ready",
                StationId = task.StationId,
                RecordedAtUtc = now,
                Reason = reason,
                EvidenceJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    request.RequestId,
                    RestoredStage = (DispatchTaskStage)task.Stage,
                    StepId = step.StepId,
                    user.WindowsIdentity
                })
            }).ExecuteCommandAsync(ct);
            result = new(true, "任务已恢复为暂停前阶段；步骤重新进入就绪，仍由原安全门禁重新检查。");
        });
        if (!transaction.IsSuccess)
            throw new InvalidOperationException("恢复暂停任务事务失败。", transaction.ErrorException);
        return result;
    }
}
