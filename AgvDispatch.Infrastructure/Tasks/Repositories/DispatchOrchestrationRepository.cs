using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class DispatchOrchestrationRepository(ISqlSugarClient database)
    : IDispatchOrchestrationRepository
{
    public async Task<IReadOnlyList<DispatchTaskWorkItem>> GetDispatchCandidatesAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = (int)DispatchTaskStage.WaitingForVehicle;
        var entities = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.Stage == stage)
            .OrderByDescending(task => task.Priority)
            .OrderBy(task => task.CreatedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        return entities.Select(ToWorkItem).ToArray();
    }

    public async Task<IReadOnlyList<DispatchTaskWorkItem>> GetSubmittingTasksAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = (int)DispatchTaskStage.SubmittingToAgv;
        var entities = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.Stage == stage)
            .OrderBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);
        return entities.Select(ToWorkItem).ToArray();
    }

    public async Task<IReadOnlyList<DispatchTaskWorkItem>> GetExecutingTasksAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entities = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.VendorTaskId != null && task.Stage < (int)DispatchTaskStage.Completed)
            .OrderBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);
        return entities.Select(ToWorkItem).ToArray();
    }

    public async Task<IReadOnlyList<DispatchTaskWorkItem>> GetAssignedTasksAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var firstAssignedStage = (int)DispatchTaskStage.GoingToPickup;
        var lastAssignedStage = (int)DispatchTaskStage.WaitingForPlcConfirmation;
        var entities = await database.Queryable<DispatchTaskEntity>()
            .Where(task =>
                task.Stage >= firstAssignedStage &&
                task.Stage <= lastAssignedStage)
            .OrderBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);
        return entities.Select(ToWorkItem).ToArray();
    }

    public async Task<IReadOnlyList<DispatchTaskStepWorkItem>> GetReadyStepsAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ready = (int)DispatchTaskStepStatus.Ready;
        var steps = await database.Queryable<DispatchTaskStepEntity>()
            .Where(step => step.Status == ready)
            .OrderBy(step => step.UpdatedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        if (steps.Count == 0)
        {
            return [];
        }

        var taskIds = steps.Select(step => step.TaskId).Distinct().ToArray();
        var parents = await database.Queryable<DispatchTaskEntity>()
            .Where(task => taskIds.Contains(task.TaskId) && task.Stage < (int)DispatchTaskStage.Completed)
            .ToListAsync(cancellationToken);
        var byId = parents.ToDictionary(task => task.TaskId, StringComparer.OrdinalIgnoreCase);
        return steps
            .Where(step => byId.ContainsKey(step.TaskId))
            .Select(step => ToStepWorkItem(byId[step.TaskId], step))
            .ToArray();
    }

    public async Task<bool> TryClaimForSubmissionAsync(
        string taskId,
        string vehicleId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var affected = await database.Updateable<DispatchTaskEntity>()
            .SetColumns(task => new DispatchTaskEntity
            {
                Stage = (int)DispatchTaskStage.SubmittingToAgv,
                VehicleId = vehicleId,
                UpdatedAt = DateTime.Now,
                ErrorMessage = null
            })
            .Where(task =>
                task.TaskId == taskId &&
                task.Stage == (int)DispatchTaskStage.WaitingForVehicle)
            .ExecuteCommandAsync(cancellationToken);
        return affected == 1;
    }

    public async Task MarkSubmittedAsync(
        string taskId,
        string vendorTaskId,
        string? vehicleId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var affected = await database.Updateable<DispatchTaskEntity>()
            .SetColumns(task => new DispatchTaskEntity
            {
                Stage = (int)DispatchTaskStage.GoingToPickup,
                VendorTaskId = vendorTaskId,
                VehicleId = vehicleId,
                UpdatedAt = DateTime.Now,
                ErrorMessage = null
            })
            .Where(task =>
                task.TaskId == taskId &&
                task.Stage == (int)DispatchTaskStage.SubmittingToAgv)
            .ExecuteCommandAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException($"任务{taskId}不在AGV提交中状态。");
        }
    }

    public async Task AdvanceStageAsync(
        string taskId,
        DispatchTaskStage targetStage,
        string? vehicleId,
        string? message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.TaskId == taskId)
            .FirstAsync(cancellationToken) ??
            throw new KeyNotFoundException($"找不到任务{taskId}。");
        var current = (DispatchTaskStage)entity.Stage;
        if (!CanAdvance(current, targetStage))
        {
            return;
        }

        var error = string.IsNullOrWhiteSpace(message)
            ? null
            : message.Length <= 1000 ? message : message[..1000];
        var affected = await database.Updateable<DispatchTaskEntity>()
            .SetColumns(task => new DispatchTaskEntity
            {
                Stage = (int)targetStage,
                VehicleId = vehicleId ?? entity.VehicleId,
                UpdatedAt = DateTime.Now,
                ErrorMessage = error
            })
            .Where(task => task.TaskId == taskId && task.Stage == (int)current)
            .ExecuteCommandAsync(cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException($"任务{taskId}状态被并发修改，请重新读取。");
        }
    }

    public async Task RecordSubmissionFailureAsync(
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
            .Where(task =>
                task.TaskId == taskId &&
                task.Stage == (int)DispatchTaskStage.SubmittingToAgv)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task CompleteExecutingStepAsync(
        string taskId,
        string vendorTaskId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item =>
                    item.TaskId == taskId &&
                    item.VendorTaskId == vendorTaskId &&
                    item.Status == (int)DispatchTaskStepStatus.Executing)
                .FirstAsync(cancellationToken) ??
                throw new InvalidOperationException(
                    $"任务{taskId}找不到执行中的AGV步骤{vendorTaskId}。");
            var targetStage = ((DispatchTaskStepType)step.StepType) switch
            {
                DispatchTaskStepType.GoToPickup => DispatchTaskStage.PickingUp,
                DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.WaitingForDumper,
                DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
                DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.WaitingForPlcConfirmation,
                _ => throw new InvalidOperationException(
                    $"步骤{step.StepId}不是可由AGV完成的运输步骤。")
            };

            var now = DateTime.Now;
            await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(item => new DispatchTaskStepEntity
                {
                    Status = (int)DispatchTaskStepStatus.Completed,
                    CompletedAt = now,
                    UpdatedAt = now,
                    ErrorMessage = null
                })
                .Where(item =>
                    item.StepId == step.StepId &&
                    item.Status == (int)DispatchTaskStepStatus.Executing)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(item => new DispatchTaskStepEntity
                {
                    Status = (int)DispatchTaskStepStatus.Ready,
                    StartedAt = now,
                    UpdatedAt = now
                })
                .Where(item =>
                    item.TaskId == taskId &&
                    item.Sequence == step.Sequence + 1 &&
                    item.Status == (int)DispatchTaskStepStatus.Pending)
                .ExecuteCommandAsync(cancellationToken);
            var taskAffected = await database.Updateable<DispatchTaskEntity>()
                .SetColumns(task => new DispatchTaskEntity
                {
                    Stage = (int)targetStage,
                    VendorTaskId = null,
                    UpdatedAt = now,
                    ErrorMessage = null
                })
                .Where(task => task.TaskId == taskId && task.VendorTaskId == vendorTaskId)
                .ExecuteCommandAsync(cancellationToken);
            if (taskAffected != 1)
            {
                throw new InvalidOperationException($"任务{taskId}的AGV步骤已被并发修改。");
            }
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"完成任务{taskId}的AGV步骤失败。",
                transaction.ErrorException);
        }
    }

    public async Task<bool> CompleteReadyStepAsync(
        string taskId,
        string stepId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = false;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item =>
                    item.TaskId == taskId &&
                    item.StepId == stepId &&
                    item.Status == (int)DispatchTaskStepStatus.Ready)
                .FirstAsync(cancellationToken);
            if (step is null)
            {
                return;
            }

            var targetStage = ((DispatchTaskStepType)step.StepType) switch
            {
                DispatchTaskStepType.PickupFullBox => DispatchTaskStage.PickingUp,
                DispatchTaskStepType.WaitForDumperReady => DispatchTaskStage.WaitingForDumper,
                DispatchTaskStepType.DumpFullBox => DispatchTaskStage.Dumping,
                DispatchTaskStepType.PlaceEmptyBox => DispatchTaskStage.WaitingForPlcConfirmation,
                DispatchTaskStepType.CompletePlcHandshake => DispatchTaskStage.Completed,
                _ => throw new InvalidOperationException(
                    $"步骤{step.StepId}不是可由PLC握手完成的步骤。")
            };

            var now = DateTime.Now;
            var affected = await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(item => new DispatchTaskStepEntity
                {
                    Status = (int)DispatchTaskStepStatus.Completed,
                    CompletedAt = now,
                    UpdatedAt = now,
                    ErrorMessage = null
                })
                .Where(item =>
                    item.StepId == stepId &&
                    item.Status == (int)DispatchTaskStepStatus.Ready)
                .ExecuteCommandAsync(cancellationToken);
            if (affected != 1)
            {
                return;
            }

            if ((DispatchTaskStepType)step.StepType != DispatchTaskStepType.CompletePlcHandshake)
            {
                var nextAffected = await database.Updateable<DispatchTaskStepEntity>()
                    .SetColumns(item => new DispatchTaskStepEntity
                    {
                        Status = (int)DispatchTaskStepStatus.Ready,
                        StartedAt = now,
                        UpdatedAt = now
                    })
                    .Where(item =>
                        item.TaskId == taskId &&
                        item.Sequence == step.Sequence + 1 &&
                        item.Status == (int)DispatchTaskStepStatus.Pending)
                    .ExecuteCommandAsync(cancellationToken);
                if (nextAffected != 1)
                {
                    throw new InvalidOperationException($"步骤{stepId}缺少可激活的下一步骤。");
                }
            }

            var taskAffected = await database.Updateable<DispatchTaskEntity>()
                .SetColumns(task => new DispatchTaskEntity
                {
                    Stage = (int)targetStage,
                    UpdatedAt = now,
                    ErrorMessage = null
                })
                .Where(task => task.TaskId == taskId)
                .ExecuteCommandAsync(cancellationToken);
            if (taskAffected != 1)
            {
                throw new InvalidOperationException($"找不到步骤{stepId}所属任务{taskId}。");
            }

            completed = true;
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"完成PLC握手步骤{stepId}失败。",
                transaction.ErrorException);
        }

        return completed;
    }

    public async Task RecordReadyStepFailureAsync(
        string taskId,
        string stepId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = errorMessage.Length <= 1000 ? errorMessage : errorMessage[..1000];
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var now = DateTime.Now;
            await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(step => new DispatchTaskStepEntity
                {
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(step =>
                    step.TaskId == taskId &&
                    step.StepId == stepId &&
                    step.Status == (int)DispatchTaskStepStatus.Ready)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<DispatchTaskEntity>()
                .SetColumns(task => new DispatchTaskEntity
                {
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(task => task.TaskId == taskId)
                .ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"记录任务步骤{stepId}失败原因时落库失败。",
                transaction.ErrorException);
        }
    }

    public async Task FailExecutingStepAsync(
        string taskId,
        string vendorTaskId,
        DispatchTaskStage terminalStage,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (terminalStage is not (DispatchTaskStage.Faulted or DispatchTaskStage.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalStage));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? null
            : errorMessage.Length <= 1000 ? errorMessage : errorMessage[..1000];
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var now = DateTime.Now;
            await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(step => new DispatchTaskStepEntity
                {
                    Status = terminalStage == DispatchTaskStage.Cancelled
                        ? (int)DispatchTaskStepStatus.Cancelled
                        : (int)DispatchTaskStepStatus.Failed,
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(step =>
                    step.TaskId == taskId &&
                    step.VendorTaskId == vendorTaskId &&
                    step.Status == (int)DispatchTaskStepStatus.Executing)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<DispatchTaskEntity>()
                .SetColumns(task => new DispatchTaskEntity
                {
                    Stage = (int)terminalStage,
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(task => task.TaskId == taskId && task.VendorTaskId == vendorTaskId)
                .ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"记录任务{taskId}的AGV步骤失败状态时落库失败。",
                transaction.ErrorException);
        }
    }

    private static bool CanAdvance(
        DispatchTaskStage current,
        DispatchTaskStage target) => target switch
        {
            DispatchTaskStage.PickingUp => current == DispatchTaskStage.GoingToPickup,
            DispatchTaskStage.GoingToDumper =>
                current is DispatchTaskStage.GoingToPickup or DispatchTaskStage.PickingUp,
            DispatchTaskStage.WaitingForDumper =>
                current is DispatchTaskStage.GoingToPickup or
                    DispatchTaskStage.PickingUp or
                    DispatchTaskStage.GoingToDumper,
            DispatchTaskStage.Faulted or DispatchTaskStage.Cancelled =>
                current is DispatchTaskStage.GoingToPickup or
                    DispatchTaskStage.PickingUp or
                    DispatchTaskStage.GoingToDumper,
            _ => false
        };

    private static DispatchTaskWorkItem ToWorkItem(DispatchTaskEntity task) => new(
        task.TaskId,
        task.StationId,
        task.VehicleId,
        task.VendorTaskId,
        (DispatchTaskStage)task.Stage,
        task.Priority,
        ToOffset(task.CreatedAt),
        ToOffset(task.UpdatedAt)) { RequestedVehicleId = task.RequestedVehicleId };

    private static DispatchTaskStepWorkItem ToStepWorkItem(
        DispatchTaskEntity task,
        DispatchTaskStepEntity step) => new(
        task.TaskId,
        task.StationId,
        task.VehicleId,
        (DispatchTaskStage)task.Stage,
        step.StepId,
        step.Sequence,
        (DispatchTaskStepType)step.StepType,
        (DispatchTaskStepStatus)step.Status,
        step.SourceSite,
        step.DestinationSite,
        ToOffset(step.UpdatedAt));

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Local));
}
