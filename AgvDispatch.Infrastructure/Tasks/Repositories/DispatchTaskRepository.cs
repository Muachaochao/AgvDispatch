using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class DispatchTaskRepository(ISqlSugarClient database) : IDispatchTaskRepository, IManualTaskCreationRepository
{
    public Task<TaskCreationResult> TryCreateAsync(
        string stationId,
        string dumperStationId,
        int priority,
        CancellationToken cancellationToken) => CreateCoreAsync(stationId, dumperStationId, priority, cancellationToken);

    public Task<TaskCreationResult> TryCreateManualAsync(ManualTaskCreationRequest request, string dumperStationId,
        string operatorIdentity, CancellationToken ct) => CreateCoreAsync(request.StationId, dumperStationId, request.Priority, ct, request, operatorIdentity);

    public async Task<ManualTaskReceipt?> FindRequestAsync(string requestId, CancellationToken ct)
    {
        var entity = await database.Queryable<DispatchTaskEntity>().Where(task => task.CreateRequestId == requestId).FirstAsync(ct);
        return entity is null ? null : new(entity.TaskId, entity.CreateRequestId!, entity.StationId, entity.RequestedVehicleId,
            entity.Priority, entity.CreateMapId!, entity.CreateMapVersionId!) { ParameterRevision = entity.CreateParameterRevision ?? "initial" };
    }

    private async Task<DispatchTaskEntity?> FindReplayAsync(ManualTaskCreationRequest request, CancellationToken ct)
    {
        var entity = await database.Queryable<DispatchTaskEntity>().Where(task => task.CreateRequestId == request.RequestId).FirstAsync(ct);
        if (entity is not null && (entity.StationId != request.StationId || entity.RequestedVehicleId != request.RequestedVehicleId ||
            entity.Priority != request.Priority || entity.CreateMapId != request.MapId || entity.CreateMapVersionId != request.MapVersionId ||
            (entity.CreateParameterRevision ?? "initial") != request.ParameterRevision))
            throw new InvalidOperationException("请求编号已用于不同参数，请重新核对任务。");
        return entity;
    }

    private async Task<TaskCreationResult> CreateCoreAsync(string stationId, string dumperStationId, int priority,
        CancellationToken cancellationToken, ManualTaskCreationRequest? request = null, string? operatorIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumperStationId);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCreationResult? result = null;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            if (request is not null && await FindReplayAsync(request, cancellationToken) is { } replay)
            { result = new(false, ToActiveTask(replay), "该请求已创建任务。"); return; }
            var existing = await FindActiveByStationAsync(stationId, cancellationToken);
            if (existing is not null)
            {
                result = new TaskCreationResult(
                    false,
                    ToActiveTask(existing),
                    "工位已有未完成任务。");
                return;
            }

            var now = DateTime.Now;
            var entity = new DispatchTaskEntity
            {
                TaskId = CreateTaskId(now),
                StationId = stationId,
                Stage = (int)DispatchTaskStage.Created,
                Priority = priority,
                RequestedVehicleId = request?.RequestedVehicleId,
                CreateRequestId = request?.RequestId,
                CreateMapId = request?.MapId,
                CreateMapVersionId = request?.MapVersionId,
                CreateParameterRevision = request?.ParameterRevision,
                CreatedBy = operatorIdentity,
                CreatedAt = now,
                UpdatedAt = now
            };
            await database.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            await database.Insertable(CreateSteps(entity, dumperStationId))
                .ExecuteCommandAsync(cancellationToken);
            result = new TaskCreationResult(true, ToActiveTask(entity));
        });

        if (!transaction.IsSuccess)
        {
            if (request is not null && await FindReplayAsync(request, cancellationToken) is { } replay)
                return new(false, ToActiveTask(replay), "该请求已创建任务。");
            // 并发插入由数据库唯一索引兜底；失败后读取胜出的活动任务。
            var existing = await FindActiveByStationAsync(stationId, cancellationToken);
            if (existing is not null)
            {
                return new TaskCreationResult(
                    false,
                    ToActiveTask(existing),
                    "并发请求已创建任务。");
            }

            throw new InvalidOperationException(
                $"创建工位{stationId}任务失败。",
                transaction.ErrorException);
        }

        return result ?? throw new InvalidOperationException("任务创建事务未返回结果。");
    }

    public async Task<IReadOnlyList<DispatchTaskStepSnapshot>> GetStepsAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        var entities = await database.Queryable<DispatchTaskStepEntity>()
            .Where(step => step.TaskId == taskId)
            .OrderBy(step => step.Sequence)
            .ToListAsync(cancellationToken);
        return entities.Select(ToStepSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<ActiveDispatchTask>> GetPendingAcknowledgementsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = (int)DispatchTaskStage.Created;
        var entities = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.Stage == created)
            .OrderBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);
        return entities.Select(ToActiveTask).ToArray();
    }

    public async Task MarkAcknowledgedAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var now = DateTime.Now;
            var affected = await database.Updateable<DispatchTaskEntity>()
                .SetColumns(task => new DispatchTaskEntity
                {
                    Stage = (int)DispatchTaskStage.WaitingForVehicle,
                    UpdatedAt = now,
                    ErrorMessage = null
                })
                .Where(task => task.TaskId == taskId && task.Stage == (int)DispatchTaskStage.Created)
                .ExecuteCommandAsync(cancellationToken);
            if (affected != 1)
            {
                throw new InvalidOperationException($"任务{taskId}不在待PLC应答状态。");
            }

            var stepAffected = await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(step => new DispatchTaskStepEntity
                {
                    Status = (int)DispatchTaskStepStatus.Ready,
                    StartedAt = now,
                    UpdatedAt = now
                })
                .Where(step =>
                    step.TaskId == taskId &&
                    step.Sequence == 1 &&
                    step.Status == (int)DispatchTaskStepStatus.Pending)
                .ExecuteCommandAsync(cancellationToken);
            if (stepAffected != 1)
            {
                throw new InvalidOperationException($"任务{taskId}缺少可激活的首步骤。");
            }
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException($"任务{taskId}PLC应答落库失败。", transaction.ErrorException);
        }
    }

    public async Task RecordAcknowledgementFailureAsync(
        string taskId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = errorMessage.Length <= 1000 ? errorMessage : errorMessage[..1000];
        await database.Updateable<DispatchTaskEntity>()
            .SetColumns(task => new DispatchTaskEntity
            {
                UpdatedAt = DateTime.Now,
                ErrorMessage = message
            })
            .Where(task => task.TaskId == taskId && task.Stage == (int)DispatchTaskStage.Created)
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task<DispatchTaskEntity?> FindActiveByStationAsync(
        string stationId,
        CancellationToken cancellationToken)
    {
        var completed = (int)DispatchTaskStage.Completed;
        var cancelled = (int)DispatchTaskStage.Cancelled;
        return await database.Queryable<DispatchTaskEntity>()
            .Where(task =>
                task.StationId == stationId &&
                task.Stage != completed &&
                task.Stage != cancelled)
            .OrderBy(task => task.CreatedAt)
            .FirstAsync(cancellationToken);
    }

    private static ActiveDispatchTask ToActiveTask(DispatchTaskEntity task) => new(
        task.TaskId,
        task.StationId,
        task.VehicleId,
        task.VendorTaskId,
        (DispatchTaskStage)task.Stage,
        new DateTimeOffset(DateTime.SpecifyKind(task.CreatedAt, DateTimeKind.Local)),
        new DateTimeOffset(DateTime.SpecifyKind(task.UpdatedAt, DateTimeKind.Local)));

    private static string CreateTaskId(DateTime createdAt) =>
        $"TASK-{createdAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

    private static DispatchTaskStepEntity[] CreateSteps(
        DispatchTaskEntity task,
        string dumperStationId)
    {
        var definitions = new[]
        {
            (DispatchTaskStepType.GoToPickup, (string?)null, task.StationId),
            (DispatchTaskStepType.PickupFullBox, task.StationId, task.StationId),
            (DispatchTaskStepType.DeliverFullBoxToDumper, task.StationId, dumperStationId),
            (DispatchTaskStepType.WaitForDumperReady, dumperStationId, dumperStationId),
            (DispatchTaskStepType.EnterDumpingPosition, dumperStationId, dumperStationId),
            (DispatchTaskStepType.DumpFullBox, dumperStationId, dumperStationId),
            (DispatchTaskStepType.ReturnEmptyBox, dumperStationId, task.StationId),
            (DispatchTaskStepType.PlaceEmptyBox, task.StationId, task.StationId),
            (DispatchTaskStepType.CompletePlcHandshake, task.StationId, (string?)null)
        };

        return definitions.Select((definition, index) => new DispatchTaskStepEntity
        {
            StepId = $"STEP-{Guid.NewGuid():N}",
            TaskId = task.TaskId,
            Sequence = index + 1,
            StepType = (int)definition.Item1,
            Status = (int)DispatchTaskStepStatus.Pending,
            SourceSite = definition.Item2,
            DestinationSite = definition.Item3,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.CreatedAt
        }).ToArray();
    }

    private static DispatchTaskStepSnapshot ToStepSnapshot(DispatchTaskStepEntity step) => new(
        step.StepId,
        step.TaskId,
        step.Sequence,
        (DispatchTaskStepType)step.StepType,
        (DispatchTaskStepStatus)step.Status,
        step.SourceSite,
        step.DestinationSite,
        step.VehicleId,
        step.VendorTaskId,
        step.RetryCount,
        ToOffset(step.CreatedAt),
        ToOffset(step.UpdatedAt),
        ToOffset(step.StartedAt),
        ToOffset(step.CompletedAt),
        step.ErrorMessage);

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Local));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value.HasValue ? ToOffset(value.Value) : null;
}
