using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskCenterReadRepository(ISqlSugarClient database) : ITaskCenterReadRepository
{
    public async Task<TaskListPage> QueryAsync(TaskListQuery query, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(query.Filter) || query.Page < 1 || query.Page > 100000 ||
            query.PageSize < 1 || query.PageSize > 100 || (query.Search?.Length ?? 0) > 128)
            throw new ArgumentException("任务查询参数无效：每页1~100条，搜索词最多128字符。");
        var search = query.Search?.Trim() ?? "";
        var source = database.Queryable<DispatchTaskEntity>();
        source = query.Filter switch
        {
            TaskListFilter.Waiting => source.Where(t => t.Stage == 0 || t.Stage == 10),
            TaskListFilter.Running => source.Where(t => t.Stage >= 15 && t.Stage <= 80),
            TaskListFilter.Failed => source.Where(t => t.Stage == 100 || t.Stage == 120 || t.Stage == 130),
            TaskListFilter.Completed => source.Where(t => t.Stage == 90),
            TaskListFilter.Cancelled => source.Where(t => t.Stage == 110),
            _ => source
        };
        if (search.Length > 0)
            source = source.Where(t => t.TaskId.Contains(search) || t.StationId.Contains(search) ||
                (t.VehicleId != null && t.VehicleId.Contains(search)));
        var count = await source.Clone().CountAsync(cancellationToken);
        var rows = await source.OrderBy(t => t.CreatedAt, OrderByType.Desc)
            .OrderBy(t => t.TaskId).Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize).ToListAsync(cancellationToken);
        return new(rows.Select(ToRow).ToArray(), count, query.Page, query.PageSize);
    }

    public Task<TaskCenterDetail?> GetDetailAsync(string taskId, CancellationToken cancellationToken) => GetDetailAsync(new TaskDetailRequest(taskId), cancellationToken);
    public async Task<TaskCenterDetail?> GetDetailAsync(TaskDetailRequest request, CancellationToken cancellationToken)
    {
        var taskId = request.TaskId;
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 64)
            throw new ArgumentException("任务编号无效。");
        var task = await database.Queryable<DispatchTaskEntity>()
            .Where(t => t.TaskId == taskId).FirstAsync(cancellationToken);
        if (task is null) return null;
        var steps = await database.Queryable<DispatchTaskStepEntity>()
            .Where(t => t.TaskId == taskId).OrderBy(t => t.Sequence).ToListAsync(cancellationToken);
        var commands = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(t => t.TaskId == taskId).OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);
        var leases = await database.Queryable<TrafficResourceLeaseEntity>()
            .Where(t => t.TaskId == taskId).OrderBy(t => t.ResourceId).ToListAsync(cancellationToken);
        var history = request.IncludeOperationHistory ? await ReadOperationHistoryAsync(taskId, cancellationToken) : (Items: (IReadOnlyList<TaskOperationHistoryEntry>)[], Total: 0);
        var timeline = await ReadTimelineAsync(taskId, cancellationToken);
        var handshakes = await database.Queryable<TaskHandshakeEntity>().Where(e=>e.TaskId==taskId).OrderBy(e=>e.StartedAtUtc).ToListAsync(cancellationToken);
        var releaseReasons = await database.Queryable<TrafficLifecycleEventEntity>().Where(e=>e.TaskId==taskId && e.Kind=="Released").OrderBy(e=>e.OccurredAt,OrderByType.Desc).Take(100).Select(e=>e.Reason).ToListAsync(cancellationToken);
        var returned = steps.Any(s=>s.StepType==70 && s.Status==50 && s.VendorTaskId!=null);
        var closure = handshakes.FirstOrDefault(h=>steps.Any(s=>s.StepId==h.StepId && s.StepType==90));
        var exitRecorded = await database.Queryable<TaskRuntimeEventEntity>().Where(e=>e.TaskId==taskId&&e.Kind=="ExitVerified").AnyAsync(cancellationToken);
        var exitChecked = exitRecorded && closure != null && closure.Phase is "AwaitDumperClosed" or "ClearEmptyResponsibility" or "ClearPlaced" or "ClearRemainingCall" or "ClearRemainingRemoved" or "VerifyClosure" or "ReadyToComplete";
        var equipmentClaims=await database.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==taskId).ToListAsync(cancellationToken);
        var handover = new TaskHandoverDiagnosis(exitChecked?"ConservativeReturnObserved":returned?"ReturnCompletedExitUnverified":"ExitUnverified",
            exitChecked?"新契约已记录返程完成及原站新鲜定位核对；不是更早的中途出区时刻。":"尚无新契约返程定位核对；命令准备、发送、到位通知清零或租期到期均不能证明物理离站。",
            returned,exitChecked,leases.Select(l=>l.ResourceId).Concat(equipmentClaims.Select(e=>"设备:"+e.EquipmentId)).Distinct().ToArray(),releaseReasons);

        return new(ToRow(task), steps.Select(s => new TaskStepDetail(s.StepId, s.Sequence,
                (DispatchTaskStepType)s.StepType, (DispatchTaskStepStatus)s.Status, s.VehicleId,
                s.SourceSite, s.DestinationSite, s.VendorTaskId, s.RetryCount,
                TimeOrNull(s.StartedAt), TimeOrNull(s.CompletedAt), s.ErrorMessage)).ToArray(),
            commands.Select(c => new TaskCommandDetail(c.CommandId, c.StepId, c.VehicleId,
                (AgvCommandStatus)c.Status, c.AttemptCount, Time(c.NextAttemptAt),
                TimeOrNull(c.SentAt), c.VendorCommandId, c.LastError)).ToArray(),
            leases.Select(l => new TaskResourceDetail(l.ResourceId, l.OwnerStepId, l.VehicleId,
                Time(l.AcquiredAt), Time(l.ExpiresAt), Time(l.ExpiresAt) < DateTimeOffset.Now)).ToArray(),
            [], "") { Handshakes=handshakes.Select(TaskHandshakeStore.Describe).ToArray(), Handover=handover, ExecutionTimeline = timeline.Items, ExecutionTimelineTotal = timeline.Total, OperationHistory = history.Items, OperationHistoryTotal = history.Total, Cancellation = task.CancelRequestId == null ? null : new(task.CancelRequestId, task.CancelReason ?? "", task.CancelRequestedBy ?? "",
                TimeOrNull(task.CancelRequestedAt), task.CancelAttemptCount ?? 0, TimeOrNull(task.CancelSentAt), TimeOrNull(task.CancelConfirmedAt)) };
    }

    private static TaskCenterRow ToRow(DispatchTaskEntity t) => new(t.TaskId, t.StationId,
        t.VehicleId, (DispatchTaskStage)t.Stage, t.Priority, Time(t.CreatedAt), Time(t.UpdatedAt), t.ErrorMessage)
        { RequestedVehicleId = t.RequestedVehicleId };
    private static DateTimeOffset Time(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Local));
    private static DateTimeOffset? TimeOrNull(DateTime? value) => value.HasValue ? Time(value.Value) : null;
}
