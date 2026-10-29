using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskCenterReadRepository(ISqlSugarClient database) : ITaskCenterReadRepository
{
    public async Task<TaskListPage> QueryAsync(TaskListQuery query, CancellationToken cancellationToken)
    {
        TaskListQueryValidation.Validate(query);
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
        // Dispatch timestamps are stored as local DateTime by the existing writer.
        // Convert offset-bearing browser input to that same frame, including UTC input.
        if (query.From is { } from)
        {
            var localFrom = from.LocalDateTime;
            source = source.Where(t => t.CreatedAt >= localFrom);
        }
        if (query.To is { } to)
        {
            var localTo = to.LocalDateTime;
            source = source.Where(t => t.CreatedAt < localTo);
        }
        var stages = await source.Clone().GroupBy(t => t.Stage)
            .Select(t => new { t.Stage, Count = SqlFunc.AggregateCount(t.TaskId) }).ToListAsync(cancellationToken);
        var count = stages.Sum(t => t.Count);
        int Sum(Func<int, bool> matches) => stages.Where(t => matches(t.Stage)).Sum(t => t.Count);
        var waiting = Sum(s => s is 0 or 10);
        var running = Sum(s => s is >= 15 and <= 80);
        var completed = Sum(s => s == 90);
        var cancelled = Sum(s => s == 110);
        var failed = Sum(s => s is 100 or 120 or 130);
        var rows = await source.OrderBy(t => t.CreatedAt, OrderByType.Desc)
            .OrderBy(t => t.TaskId).Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize).ToListAsync(cancellationToken);
        return new(rows.Select(ToRow).ToArray(), count, query.Page, query.PageSize)
        {
            Summary = new(count, running, waiting, completed, cancelled, failed,
                count - running - waiting - completed - cancelled - failed)
        };
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
        var exit = handshakes.FirstOrDefault(h=>h.ContractVersion==HandshakeExecutionOptions.Version && steps.Any(s=>s.StepId==h.StepId && s.StepType==70));
        var exitRecorded = exit!=null && await database.Queryable<TaskRuntimeEventEntity>().Where(e=>e.TaskId==taskId&&e.StepId==exit.StepId&&e.Kind=="ExitVerified"&&e.Phase=="ClearDumpingAfterExit").AnyAsync(cancellationToken);
        var exitChecked = exitRecorded && exit != null && exit.Phase is "ClearDumpingAfterExit" or "AwaitDumperClosed" or "ReleaseDumper" or "ExitHandoverComplete";
        var handedOver = exitChecked && exit is { Phase:"ExitHandoverComplete", PendingSignal:null };
        var equipmentClaims=await database.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==taskId).ToListAsync(cancellationToken);
        var handover = new TaskHandoverDiagnosis(handedOver?"ExitHandoverComplete":exitChecked?"PhysicalExitVerified":returned?"ReturnCompletedExitUnverified":"ExitUnverified",
            handedOver?"28阶段已持久化实际出区证据、PLC退出闭合及本任务倒料设备/已验证区域交接；返箱责任继续保持，其他道路占用以实际记录为准。":exitChecked?"28阶段已记录实际出区证据，仍需核对PLC周期闭合和设备/区域交接；物理出区本身不等于后车已获准进入。":"尚无28阶段实际出区核对；返回命令准备、接受、完成、通知清零或租期到期均不能单独证明出区交接。",
            returned,exitChecked,leases.Select(l=>l.ResourceId).Concat(equipmentClaims.Select(e=>"设备:"+e.EquipmentId)).Distinct().ToArray(),releaseReasons);
        if(exit!=null&&!handedOver)
            handover=handover with {Reason=$"原返程交接阶段：{exit.Phase}；修订{exit.Revision}。{exit.Reason} "+handover.Reason};

        return new(ToRow(task), steps.Select(s => new TaskStepDetail(s.StepId, s.Sequence,
                (DispatchTaskStepType)s.StepType, (DispatchTaskStepStatus)s.Status, s.VehicleId,
                s.SourceSite, s.DestinationSite, s.VendorTaskId, s.RetryCount,
                TimeOrNull(s.StartedAt), TimeOrNull(s.CompletedAt), s.ErrorMessage)).ToArray(),
            commands.Select(c => new TaskCommandDetail(c.CommandId, c.StepId, c.VehicleId,
                (AgvCommandStatus)c.Status, c.AttemptCount, Time(c.NextAttemptAt),
                TimeOrNull(c.SentAt), c.VendorCommandId, c.LastError){Route=ReadRoute(c)}).ToArray(),
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
