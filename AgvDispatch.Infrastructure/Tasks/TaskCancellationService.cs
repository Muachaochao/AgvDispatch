using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence;

// The shared control gate serializes this intent with PLC writers and transport senders.
// HTTP acceptance never means vehicle cancellation has been confirmed.
public sealed class TaskCancellationService(ISqlSugarClient database, IMapExecutionGate gate, IAgvGateway gateway,
    IAgvStateCache? vehicles = null) : ITaskCancellationService
{
    public async Task<TaskCancelResult> RequestAsync(TrafficUserContext user, TaskCancelRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("需要本地任务操作权限。");
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 64 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 1000)
            return new(false, "请填写有效请求编号及至少10字、最多1000字的取消依据。");
        using var lease = await gate.EnterControlAsync(ct);
        var replay = await database.Queryable<DispatchTaskEntity>().Where(t => t.CancelRequestId == request.RequestId).FirstAsync(ct);
        if (replay != null) return replay.TaskId == request.TaskId && replay.CancelReason == request.Reason.Trim() &&
            replay.CancelOriginalStage == (int)request.ExpectedStage && replay.CancelRequiresPickupNavigation == request.RequirePickupNavigation
            ? new(true, replay.ErrorMessage ?? "取消请求已记录。") : new(false, "请求编号已用于不同取消参数。");
        var task = await database.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == request.TaskId).FirstAsync(ct);
        if (task == null || task.Stage != (int)request.ExpectedStage) return new(false, "任务状态已变化，请刷新后重新核对。");
        if (task.CancelRequestId != null || task.Stage is 90 or 110 or 120 or 130) return new(false, "任务已结束或已有取消请求，请查看原请求结果。");
        var steps = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId).OrderBy(s => s.Sequence).ToListAsync(ct);
        var current = steps.FirstOrDefault(s => s.Status != (int)DispatchTaskStepStatus.Completed);
        var commands = await database.Queryable<AgvCommandOutboxEntity>().Where(c => c.TaskId == task.TaskId).ToListAsync(ct);
        var command = commands.FirstOrDefault(c => c.StepId == current?.StepId && c.CommandType == (int)AgvCommandType.Transport);
        if (request.RequirePickupNavigation &&
            (task.Stage != (int)DispatchTaskStage.GoingToPickup || steps.Count == 0 ||
             steps[0].StepType != (int)DispatchTaskStepType.GoToPickup ||
             steps[0].Status != (int)DispatchTaskStepStatus.Executing ||
             steps.Skip(1).Any(s => s.Status != (int)DispatchTaskStepStatus.Pending)))
            return new(false, "第一阶段受控换单仅允许前往取箱首段导航执行中且尚未发生取箱；其他阶段保持原取消与恢复流程。");
        if (commands.Any(c => c.StepId != current?.StepId && (c.Status == (int)AgvCommandStatus.Sending ||
            c.AttemptCount > 0 && !steps.Any(s => s.StepId == c.StepId && s.Status == (int)DispatchTaskStepStatus.Completed))))
            return new(false, "存在其他步骤发送结果不明，须先进行现场恢复核对。");
        task.CancelOriginalStage = task.Stage; task.CancelRequestId = request.RequestId; task.CancelReason = request.Reason.Trim(); task.CancelRequestedBy = user.WindowsIdentity;
        task.CancelRequestedAt = DateTime.Now; task.CancelNextAttemptAt = DateTime.Now; task.CancelAttemptCount = 0;
        task.CancelStepId = current?.StepId; task.CancelVehicleId = current?.VehicleId ?? task.VehicleId ?? command?.VehicleId;
        task.CancelVendorId = current?.VendorTaskId ?? command?.VendorCommandId;
        task.CancelRequiresPickupNavigation = request.RequirePickupNavigation;
        var possiblySent = task.CancelVendorId != null || command is { AttemptCount: > 0 } || current?.Status == (int)DispatchTaskStepStatus.Executing;
        // A lost send response is queried by the original stable business key, never treated as unsent.
        if (possiblySent && task.CancelVendorId == null) task.CancelVendorId = current?.StepId;
        task.Stage = (int)(possiblySent ? DispatchTaskStage.CancellationPending : DispatchTaskStage.CancellationReview);
        if (!possiblySent) task.CancelConfirmedAt = DateTime.Now;
        task.ErrorMessage = possiblySent ? "取消请求已记录：后续步骤已冻结，等待车辆原任务确认。" : "原运输命令未发送；已冻结后续步骤，等待载荷、PLC和路权现场核对。";
        task.UpdatedAt = DateTime.Now;
        var result = await database.Ado.UseTranAsync(async () =>
        {
            await database.Updateable(task).ExecuteCommandAsync(ct);
            await database.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity
                { Status = (int)DispatchTaskStepStatus.ManualIntervention, UpdatedAt = DateTime.Now })
                .Where(s => s.TaskId == task.TaskId && s.Status != (int)DispatchTaskStepStatus.Completed).ExecuteCommandAsync(ct);
            await database.Updateable<AgvCommandOutboxEntity>().SetColumns(c => new AgvCommandOutboxEntity
                { Status = (int)AgvCommandStatus.Cancelled, UpdatedAt = DateTime.Now, LastError = "任务取消请求冻结原发送队列。" })
                .Where(c => c.TaskId == task.TaskId && c.Status != (int)AgvCommandStatus.Sent).ExecuteCommandAsync(ct);
            await AuditAsync(task, "TaskCancellationRequested", user.WindowsIdentity, new { request, steps, commands }, ct);
        });
        if (!result.IsSuccess) throw new InvalidOperationException("取消请求事务失败，未确认冻结。", result.ErrorException);
        return new(true, task.ErrorMessage);
    }
    public async Task ProcessAsync(CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        var tasks = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage == (int)DispatchTaskStage.CancellationPending).Take(20).ToListAsync(ct);
        foreach (var task in tasks)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(task.CancelVendorId) || string.IsNullOrWhiteSpace(task.CancelStepId) || string.IsNullOrWhiteSpace(task.CancelVehicleId) || task.CancelRequestedAt == null)
                { task.ErrorMessage = "取消目标编号或车辆归属不完整；保持冻结，需现场核对。"; await SaveAsync(task, ct); continue; }
                var cancelStep = await database.Queryable<DispatchTaskStepEntity>()
                    .Where(s => s.TaskId == task.TaskId && s.StepId == task.CancelStepId).FirstAsync(ct);
                var cancelCommand = await database.Queryable<AgvCommandOutboxEntity>()
                    .Where(c => c.TaskId == task.TaskId && c.StepId == task.CancelStepId &&
                        c.CommandType == (int)AgvCommandType.Transport).FirstAsync(ct);
                AgvRobotSnapshot? cancelVehicle = null;
                var vehicleStopped = vehicles is not null &&
                    vehicles.TryGet(task.CancelVehicleId, out cancelVehicle) &&
                    cancelVehicle.Connection == ConnectionState.Online &&
                    cancelVehicle.Quality == SignalQuality.Good &&
                    cancelVehicle.Blocked == false &&
                    cancelVehicle.EmergencyStopped == false &&
                    !cancelVehicle.Tasks.Any(status => status.Status is 1 or 2 or 3);
                var noVendorTaskWasCreated =
                    string.Equals(task.CancelVendorId, task.CancelStepId, StringComparison.Ordinal) &&
                    cancelStep is not null && string.IsNullOrWhiteSpace(cancelStep.VendorTaskId) &&
                    cancelCommand is not null && string.IsNullOrWhiteSpace(cancelCommand.VendorCommandId) &&
                    cancelCommand.SentAt is null &&
                    cancelCommand.LastError?.StartsWith("AGV_PRE_SEND_REJECTED;", StringComparison.Ordinal) == true &&
                    vehicleStopped;
                if (noVendorTaskWasCreated)
                {
                    task.Stage = (int)DispatchTaskStage.CancellationReview;
                    task.CancelConfirmedAt = DateTime.Now;
                    task.ErrorMessage = "车端未创建任务：发送被明确拒绝或无厂家任务号，车辆无活动任务且已停稳；进入现场核对关闭，禁止补发原命令。";
                    var result = await database.Ado.UseTranAsync(async () =>
                    {
                        await SaveAsync(task, ct);
                        await AuditAsync(task, "TaskCancellationNoVendorTaskConfirmed", "service",
                            new { task.CancelVendorId, task.CancelStepId, task.CancelVehicleId,
                                CommandStatus = cancelCommand!.Status, cancelCommand.AttemptCount,
                                cancelCommand.LastError, VehicleTasks = cancelVehicle!.Tasks },
                            ct);
                    });
                    if (!result.IsSuccess)
                    {
                        task.Stage = (int)DispatchTaskStage.CancellationPending;
                        task.CancelConfirmedAt = null;
                        throw new InvalidOperationException("无厂家任务确认未提交，保持取消等待。", result.ErrorException);
                    }
                    continue;
                }
                var status = await gateway.GetTransportTaskStatusAsync(task.CancelVendorId, ct);
                var now = DateTimeOffset.Now;
                if (status == null || status.VendorTaskId != task.CancelVendorId || status.BusinessTaskId != task.CancelVendorId ||
                    status.VehicleId != task.CancelVehicleId || status.UpdatedAt < new DateTimeOffset(task.CancelRequestedAt!.Value) ||
                    status.UpdatedAt > now.AddSeconds(1) || now - status.UpdatedAt > TimeSpan.FromSeconds(10))
                { task.ErrorMessage = "车辆原任务记录缺失、过期或归属不符；不能把404或接口返回当作取消成功。"; await SaveAsync(task, ct); continue; }
                if (status.State is AgvTransportTaskState.Cancelled or AgvTransportTaskState.Completed or AgvTransportTaskState.Failed)
                {
                    task.Stage = (int)DispatchTaskStage.CancellationReview; task.CancelConfirmedAt = DateTime.Now;
                    task.ErrorMessage = $"车辆原任务确认：{status.State}；后续步骤保持冻结，请核对载荷、PLC和路权后关闭取消。";
                    var result = await database.Ado.UseTranAsync(async () => { await SaveAsync(task, ct); await AuditAsync(task, "TaskCancellationVehicleConfirmed", "service", status, ct); });
                    if (!result.IsSuccess)
                    {
                        task.Stage = (int)DispatchTaskStage.CancellationPending; task.CancelConfirmedAt = null;
                        throw new InvalidOperationException("车辆结束确认与审计未提交，保持取消等待。", result.ErrorException);
                    }
                    continue;
                }
                if (status.State == AgvTransportTaskState.Unknown)
                { task.ErrorMessage = "车辆原任务状态未知，保留取消等待。"; await SaveAsync(task, ct); continue; }
                if (task.CancelSentAt != null || task.CancelNextAttemptAt > DateTime.Now) continue;
                // Save intent before external side effect. A crash/lost response retries only after re-querying the same task.
                task.CancelAttemptCount = (task.CancelAttemptCount ?? 0) + 1;
                task.CancelNextAttemptAt = DateTime.Now.AddSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(task.CancelAttemptCount.Value - 1, 4))));
                await SaveAsync(task, ct);
                await AuditAsync(task, "TaskCancellationSendIntent", "service", status, ct);
                await gateway.CancelTaskAsync(task.CancelVendorId, ct);
                task.CancelSentAt = DateTime.Now; task.ErrorMessage = "车辆取消接口已接受；等待原任务状态确认，尚未关闭业务任务。";
                var accepted = await database.Ado.UseTranAsync(async () =>
                {
                    await SaveAsync(task, ct);
                    await AuditAsync(task, "TaskCancellationSendAccepted", "service", new { task.CancelVendorId, task.CancelStepId, task.CancelVehicleId, task.CancelAttemptCount }, ct);
                });
                if (!accepted.IsSuccess)
                {
                    task.CancelSentAt = null;
                    throw new InvalidOperationException("取消接受状态与审计未提交，须继续核对原车辆任务。", accepted.ErrorException);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            { task.ErrorMessage = "取消结果待确认：" + error.Message; if (task.ErrorMessage.Length > 1000) task.ErrorMessage = task.ErrorMessage[..1000]; await SaveAsync(task, ct);
                await AuditAsync(task, "TaskCancellationProcessingFailed", "service", new { Error = task.ErrorMessage, task.CancelVendorId, task.CancelAttemptCount }, ct); }
        }
    }
    private Task<int> SaveAsync(DispatchTaskEntity task, CancellationToken ct)
    { task.UpdatedAt = DateTime.Now; return database.Updateable(task).ExecuteCommandAsync(ct); }
    private Task<int> AuditAsync(DispatchTaskEntity task, string kind, string user, object evidence, CancellationToken ct) =>
        database.Insertable(new ExecutionEvidenceEntity { Id = Guid.NewGuid().ToString("N"), TaskId = task.TaskId, StepId = task.CancelStepId ?? "",
            SessionId = "", Operator = user, Kind = kind, Reason = task.CancelReason ?? "", Evidence = JsonSerializer.Serialize(evidence), OccurredAt = DateTime.Now }).ExecuteCommandAsync(ct);
}
