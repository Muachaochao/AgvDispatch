using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class ManualReleaseWorkflowService(
    ISqlSugarClient database,
    ManualReleaseWorkflowSettings settings, IAgvStateCache? vehicles = null, MapSwitchSafetySettings? safety = null) : IManualReleaseWorkflowService
{
    public async Task<IReadOnlyList<ManualReleaseWorkflowItem>> GetRequestsAsync(
        TrafficUserContext user,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        if (maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await ExpirePendingRequestsAsync(cancellationToken);
        var rows = await database.Queryable<ManualTrafficReleaseRequestEntity>()
            .OrderByDescending(item => item.RequestedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        return rows.Select(ToItem).ToArray();
    }

    public async Task<ManualReleaseWorkflowResult> SubmitAsync(
        TrafficUserContext user,
        SubmitManualReleaseRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.SubmitManualRelease);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var confirmation = request.ConfirmationText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(request.LeaseId))
        {
            return Rejected("未选择需要释放的路权记录。");
        }
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
        {
            return Rejected("人工解锁原因不能为空且至少需要5个字符。");
        }
        if (!request.VehicleClearConfirmed)
        {
            return Rejected("必须现场确认AGV已经离开该交通资源区域。");
        }

        var result = Rejected("人工解锁申请创建失败。");
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await ExpirePendingRequestsAsync(cancellationToken);
            var lease = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(item => item.LeaseId == request.LeaseId)
                .FirstAsync(cancellationToken);
            if (lease is null)
            {
                result = Rejected("路权记录不存在或已经被释放。");
                return;
            }

            var expectedPhrase = $"RELEASE {lease.ResourceId}";
            if (!string.Equals(confirmation, expectedPhrase,
                    StringComparison.OrdinalIgnoreCase))
            {
                result = Rejected($"二次确认文本不正确，请输入：{expectedPhrase}");
                return;
            }

            var task = await GetTerminalTaskAsync(lease.TaskId, cancellationToken);
            if (!task.Allowed)
            {
                result = Rejected(task.Message);
                return;
            }

            var now = DateTime.Now;
            var requestId = $"MRR-{Guid.NewGuid():N}";
            await database.Insertable(new TrafficResourceReleaseAuditEntity
            {
                AuditId = $"TRA-{Guid.NewGuid():N}", LeaseId = lease.LeaseId,
                ResourceId = lease.ResourceId, OwnerStepId = lease.OwnerStepId,
                TaskId = lease.TaskId, VehicleId = lease.VehicleId,
                Operator = Truncate(user.DisplayName, 64),
                RequestedBy = Truncate(user.WindowsIdentity, 256),
                ApprovedBy = null,
                Reason = Truncate(request.Reason.Trim(), 1000), PreviousTaskStage = task.Stage,
                HeartbeatExpired = lease.ExpiresAt < now, AcquiredAt = lease.AcquiredAt,
                RenewedAt = lease.RenewedAt, ExpiresAt = lease.ExpiresAt, ReleasedAt = now
            }).ExecuteCommandAsync(cancellationToken);
            var deleted = await database.Deleteable<TrafficResourceLeaseEntity>()
                .Where(item => item.LeaseId == lease.LeaseId)
                .ExecuteCommandAsync(cancellationToken);
            if (deleted != 1) throw new InvalidOperationException("路权已被并发修改，解锁事务已回滚。");
            await database.Insertable(new TrafficLifecycleEventEntity
            {
                EventId=Guid.NewGuid().ToString("N"), StepId=lease.OwnerStepId, TaskId=lease.TaskId,
                VehicleId=lease.VehicleId, Kind="ResourceReleased",
                Reason="人工核验释放（审计另存）："+Truncate(request.Reason.Trim(),1000),
                ResourceIds=System.Text.Json.JsonSerializer.Serialize(new[]{lease.ResourceId}), OccurredAt=now
            }).ExecuteCommandAsync(cancellationToken);
            await database.Insertable(new ManualTrafficReleaseRequestEntity
            {
                RequestId = requestId,
                LeaseId = lease.LeaseId,
                ResourceId = lease.ResourceId,
                TaskId = lease.TaskId,
                VehicleId = lease.VehicleId,
                RequestedBy = Truncate(user.WindowsIdentity, 256),
                RequestedByDisplayName = Truncate(user.DisplayName, 128),
                Reason = Truncate(request.Reason.Trim(), 1000),
                VehicleClearConfirmed = true,
                ConfirmationText = Truncate(confirmation, 256),
                Status = (int)ManualReleaseWorkflowStatus.Executed,
                RequestedAt = now,
                ExpiresAt = now,
                ReviewedAt = now,
                ReviewComment = "单人授权解锁，已校验并执行。"
            }).ExecuteCommandAsync(cancellationToken);
            result = new ManualReleaseWorkflowResult(
                true,
                $"交通资源{lease.ResourceId}已释放，已保存操作人及原因的审计记录。",
                requestId);
        });

        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException("人工解锁事务失败。", transaction.ErrorException);
        }
        return result;
    }

    // Compatibility endpoint only: old pending requests must never release a lease without a fresh confirmation.
    public Task<ManualReleaseWorkflowResult> ReviewAsync(
        TrafficUserContext user, ReviewManualReleaseRequest request, CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Rejected("双人复核流程已停用，请重新选择路权并现场确认后单人解锁。", request.RequestId));
    }

    private async Task ExpirePendingRequestsAsync(CancellationToken cancellationToken)
    {
        _ = settings; // Kept for compatibility with existing service registration.
        var now = DateTime.Now;
        await database.Updateable<ManualTrafficReleaseRequestEntity>()
            .SetColumns(item => new ManualTrafficReleaseRequestEntity
            {
                Status = (int)ManualReleaseWorkflowStatus.Expired,
                ReviewedAt = now,
                ReviewComment = "双人复核流程已停用，需现场重新确认并解锁。"
            })
            .Where(item => item.Status == (int)ManualReleaseWorkflowStatus.PendingApproval)
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task<(bool Allowed, string Message, int? Stage)> GetTerminalTaskAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var task = await database.Queryable<DispatchTaskEntity>()
            .Where(item => item.TaskId == taskId)
            .FirstAsync(cancellationToken);
        if (task is null)
        {
            return (false, $"任务{taskId}记录缺失，无法证明任务已终止。", null);
        }
        var stage = (DispatchTaskStage)task.Stage;
        if (stage == DispatchTaskStage.CancellationReview && task.CancelConfirmedAt != null)
        {
            var age = safety?.StateMaxAge ?? TimeSpan.FromSeconds(10);
            var tolerance = safety?.StoppedVelocityTolerance ?? 0.02;
            if (task.CancelVehicleId == null || vehicles == null || !vehicles.TryGet(task.CancelVehicleId, out var vehicle) ||
                vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good || vehicle.LastSuccessfulPollAt == null ||
                DateTimeOffset.Now - vehicle.LastSuccessfulPollAt > age || vehicle.LastSuccessfulPollAt > DateTimeOffset.Now.AddSeconds(1) ||
                vehicle.VelocityX == null || vehicle.VelocityY == null || vehicle.AngularVelocity == null ||
                !double.IsFinite(vehicle.VelocityX.Value) || !double.IsFinite(vehicle.VelocityY.Value) || !double.IsFinite(vehicle.AngularVelocity.Value) ||
                Math.Abs(vehicle.VelocityX.Value) > tolerance || Math.Abs(vehicle.VelocityY.Value) > tolerance || Math.Abs(vehicle.AngularVelocity.Value) > tolerance ||
                vehicle.Tasks.Any(t => t.Status is 1 or 2 or 3))
                return (false, "取消核对任务缺少新鲜停车证据或车辆仍有活动任务，禁止释放原路权。", task.Stage);
            return (true, string.Empty, task.Stage);
        }
        return stage is DispatchTaskStage.Completed or DispatchTaskStage.Faulted or DispatchTaskStage.Cancelled
            ? (true, string.Empty, task.Stage)
            : (false, $"任务{taskId}仍处于{stage}，禁止人工释放路权。", task.Stage);
    }


    private static ManualReleaseWorkflowItem ToItem(ManualTrafficReleaseRequestEntity row) => new(
        row.RequestId, row.LeaseId, row.ResourceId, row.TaskId, row.VehicleId,
        row.RequestedBy, row.RequestedByDisplayName, row.Reason,
        (ManualReleaseWorkflowStatus)row.Status,
        new DateTimeOffset(row.RequestedAt), new DateTimeOffset(row.ExpiresAt),
        row.ReviewedBy,
        row.ReviewedAt.HasValue ? new DateTimeOffset(row.ReviewedAt.Value) : null,
        row.ReviewComment);

    private static void Demand(TrafficUserContext user, TrafficPermission permission)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.Has(permission))
        {
            throw new UnauthorizedAccessException($"当前软件账号没有{permission}权限。");
        }
    }

    private static ManualReleaseWorkflowResult Rejected(string message, string? requestId = null) =>
        new(false, message, requestId);

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
