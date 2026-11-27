using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class AgvCommandOutboxRepository
    : IAgvCommandOutboxRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISqlSugarClient _database;
    private readonly IActiveMapRuntime? activeMap;
    private readonly DownstreamAdmissionOptions? admission;

    public AgvCommandOutboxRepository(ISqlSugarClient database, IActiveMapRuntime? activeMap = null,
        DownstreamAdmissionOptions? admission = null)
    {
        _database = database;
        this.activeMap = activeMap;
        this.admission = admission;
    }

    private ISqlSugarClient database => _database;

    public async Task<DownstreamAdmissionSnapshot> GetPickupAdmissionAsync(string taskId, CancellationToken ct)
    {
        if (admission?.Enabled != true) return new(false, true, 0, [], "未启用下游准入限流。");
        if (admission.Capacity < 1) throw new InvalidOperationException("下游准入容量必须大于0。");
        // Admission is committed by the first-command transaction below. A restart
        // cannot forget tickets; a fault/cancellation review does not free one.
        // Conservatively retain through verified business closure (including return).
        var leaseTasks = await database.Queryable<TrafficResourceLeaseEntity>().Select(l => l.TaskId).Distinct().ToListAsync(ct);
        var owners = await database.Queryable<DispatchTaskEntity>()
            .Where(t => t.VehicleId != null && (t.Stage >= (int)DispatchTaskStage.SubmittingToAgv &&
                t.Stage != (int)DispatchTaskStage.Completed && t.Stage != (int)DispatchTaskStage.Cancelled || leaseTasks.Contains(t.TaskId)))
            .Select(t => t.TaskId).ToListAsync(ct);
        var allowed = owners.Contains(taskId, StringComparer.OrdinalIgnoreCase) || owners.Count < admission.Capacity;
        return new(true, allowed, admission.Capacity, owners,
            allowed ? $"下游准入{owners.Count}/{admission.Capacity}，发车仍须独立核验路权和PLC。" :
                $"下游准入已满{owners.Count}/{admission.Capacity}，等待原任务核验收尾；本单保持未取箱，不提前占用候车位或倒料路权。占用任务：{string.Join('、', owners)}");
    }

    public async Task<bool> TryPrepareTransportStepAsync(
        string taskId,
        string stepId,
        string vehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        CancellationToken cancellationToken)
        => await TryPrepareTransportWindowAsync(taskId, stepId, vehicleId, path[0], path[^1],
            new AgvTransportRouteWindow(0, path[0], path[^1], path, [], [], true),
            [], null, action, cancellationToken);

    public async Task<bool> TryPrepareTransportWindowAsync(
        string taskId,
        string stepId,
        string vehicleId,
        string fullSource,
        string fullDestination,
        AgvTransportRouteWindow window,
        IReadOnlyList<AgvTransportRouteWindow> windows,
        string? previousVendorTaskId,
        AgvTransportAction? action,
        CancellationToken cancellationToken)
    {
        await using var databaseLease =
            await DatabaseTransactionGate.EnterAsync(_database, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullDestination);
        ArgumentNullException.ThrowIfNull(window);
        var path = window.Path;
        if (path.Count < 2 || path.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("AGV路径至少需要两个有效站点。", nameof(path));
        }
        if (window.Source != path[0] || window.Destination != path[^1] ||
            (window.EdgeIds.Count > 0 && window.EdgeIds.Count != path.Count - 1) || window.Index < 0)
            throw new ArgumentException("运输窗口的起终点或有序路段不一致。", nameof(window));

        if (path.Zip(path.Skip(1)).Any(pair =>
                string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("AGV路径不能包含相邻的重复站点。", nameof(path));
        }

        var sourceSite = path[0];
        var destinationSite = path[^1];
        cancellationToken.ThrowIfCancellationRequested();

        var prepared = false;
        Func<ISqlSugarClient, Task<DbResult<bool>>> transactionBody = database => database.Ado.UseTranAsync(async () =>
        {
            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item =>
                    item.TaskId == taskId &&
                    item.StepId == stepId)
                .FirstAsync(cancellationToken);
            if (step is null)
            {
                return;
            }
            if (window.Index == 0 && step.StepType == (int)DispatchTaskStepType.GoToPickup &&
                !(await GetPickupAdmissionAsync(taskId, cancellationToken)).Allowed) return;

            // Assignment snapshots are advisory. Enforce vehicle ownership in
            // the same transaction that registers the persistent command.
            if (await database.Queryable<DispatchTaskEntity>().AnyAsync(task =>
                    task.TaskId != taskId && task.VehicleId == vehicleId &&
                    task.Stage >= (int)DispatchTaskStage.SubmittingToAgv &&
                    task.Stage <= (int)DispatchTaskStage.WaitingForPlcConfirmation, cancellationToken) ||
                await database.Queryable<TrafficResourceLeaseEntity>().AnyAsync(lease =>
                    lease.TaskId != taskId && lease.VehicleId == vehicleId, cancellationToken)) return;

            var expectedTaskStage = ((DispatchTaskStepType)step.StepType) switch
            {
                DispatchTaskStepType.GoToPickup => DispatchTaskStage.WaitingForVehicle,
                DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.PickingUp,
                DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.WaitingForDumper,
                DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.Dumping,
                _ => throw new InvalidOperationException(
                    $"步骤{step.StepId}不是可发送的AGV运输步骤。")
            };

            var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
            if (window.Index == 0)
            {
                if (step.Status != (int)DispatchTaskStepStatus.Ready) return;
                var taskAffected = await database.Updateable<DispatchTaskEntity>()
                    .SetColumns(task => new DispatchTaskEntity
                    {
                        Stage = (int)DispatchTaskStage.SubmittingToAgv,
                        VehicleId = vehicleId,
                        UpdatedAt = now,
                        ErrorMessage = null
                    })
                    .Where(task =>
                        task.TaskId == taskId &&
                        task.Stage == (int)expectedTaskStage)
                    .ExecuteCommandAsync(cancellationToken);
                if (taskAffected != 1) return;

                var stepAffected = await database.Updateable<DispatchTaskStepEntity>()
                    .SetColumns(item => new DispatchTaskStepEntity
                    {
                        Status = (int)DispatchTaskStepStatus.CommandPending,
                        SourceSite = fullSource,
                        DestinationSite = fullDestination,
                        VehicleId = vehicleId,
                        UpdatedAt = now,
                        ErrorMessage = null
                    })
                    .Where(item =>
                        item.StepId == step.StepId &&
                        item.Status == (int)DispatchTaskStepStatus.Ready)
                    .ExecuteCommandAsync(cancellationToken);
                if (stepAffected != 1)
                    throw new InvalidOperationException($"任务{taskId}的运输步骤已被并发修改。");
            }
            else
            {
                var definition = activeMap?.Current?.Definition;
                var sameSource = MapRouteSiteMapper.IsSameNode(
                    definition, step.SourceSite, fullSource);
                var sameDestination = MapRouteSiteMapper.IsSameNode(
                    definition, step.DestinationSite, fullDestination);
                if (step.Status != (int)DispatchTaskStepStatus.Executing ||
                    string.IsNullOrWhiteSpace(previousVendorTaskId) ||
                    step.VendorTaskId != previousVendorTaskId || step.VehicleId != vehicleId ||
                    !sameSource || !sameDestination) return;
                var task = await database.Queryable<DispatchTaskEntity>()
                    .Where(item => item.TaskId == taskId)
                    .FirstAsync(cancellationToken);
                if (task is null || task.VehicleId != vehicleId || task.VendorTaskId != previousVendorTaskId) return;
                var open = await database.Queryable<AgvCommandOutboxEntity>().AnyAsync(item =>
                    item.TaskId == taskId && item.StepId == stepId &&
                    (item.Status == (int)AgvCommandStatus.Pending ||
                     item.Status == (int)AgvCommandStatus.Sending ||
                     item.Status == (int)AgvCommandStatus.RetryScheduled), cancellationToken);
                if (open) return;
            }

            var windowTaskId = windows.Count > 1 ? $"{stepId}-W{window.Index + 1:D2}" : stepId;
            var payload = new AgvTransportCommandPayload(
                windowTaskId,
                vehicleId,
                sourceSite,
                destinationSite,
                path.ToArray(),
                window.Final ? action : null)
            {
                BusinessStepId = stepId,
                WindowIndex = window.Index,
                WindowCount = Math.Max(windows.Count, window.Index + 1),
                PreviousVendorTaskId = previousVendorTaskId,
                FinalAction = action,
                Windows = windows
            };
            var commandId = $"CMD-{Guid.NewGuid():N}";
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var mapSnapshot = activeMap?.Current;
            var originalRoute = mapSnapshot is null ? null : await database.Queryable<TrafficRequestEntity>()
                .Where(r => r.StepId == stepId).FirstAsync(cancellationToken);
            // Legacy/no-map preparation remains supported, but never becomes eligible for segment release.
            var routeSnapshotJson = mapSnapshot is not null && originalRoute is not null
                ? TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture(taskId,stepId,commandId,vehicleId,payloadJson,mapSnapshot,originalRoute))
                : null;
            await database.Insertable(new AgvCommandOutboxEntity
            {
                CommandId = commandId,
                IdempotencyKey = window.Index == 0
                    ? $"STEP:{step.StepId}:TRANSPORT"
                    : $"STEP:{step.StepId}:TRANSPORT:W{window.Index + 1:D2}",
                TaskId = taskId,
                StepId = step.StepId,
                VehicleId = vehicleId,
                CommandType = (int)AgvCommandType.Transport,
                Status = (int)AgvCommandStatus.Pending,
                PayloadJson = payloadJson,
                ExecutionMapId = mapSnapshot?.MapId,
                ExecutionMapVersionId = mapSnapshot?.VersionId,
                ExecutionRouteSnapshotJson = routeSnapshotJson,
                ExecutionRouteSnapshotHash = routeSnapshotJson is null ? null : TransportRouteSnapshots.Hash(routeSnapshotJson),
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now
            }).ExecuteCommandAsync(cancellationToken);
            prepared = true;
        });
        var transaction = await DatabaseTransactionGate.RunIsolatedAsync(_database, transactionBody, cancellationToken);

        if (!transaction.IsSuccess)
        {
            var exists = await database.Queryable<AgvCommandOutboxEntity>()
                .AnyAsync(item => item.StepId == stepId, cancellationToken);
            if (exists)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"为任务{taskId}创建AGV命令失败。",
                transaction.ErrorException);
        }

        return prepared;
    }

    public async Task<AgvCommandOutboxItem?> GetTransportCommandByVendorAsync(string taskId,
        string vendorTaskId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item => item.TaskId == taskId && item.VendorCommandId == vendorTaskId &&
                item.CommandType == (int)AgvCommandType.Transport)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToItem(row);
    }

    public async Task<AgvCommandOutboxItem?> GetOpenTransportCommandAsync(string taskId,
        CancellationToken cancellationToken)
    {
        var row = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item => item.TaskId == taskId &&
                item.CommandType == (int)AgvCommandType.Transport &&
                (item.Status == (int)AgvCommandStatus.Pending ||
                 item.Status == (int)AgvCommandStatus.Sending ||
                 item.Status == (int)AgvCommandStatus.RetryScheduled))
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToItem(row);
    }

    public async Task<IReadOnlyList<AgvCommandOutboxItem>> GetDueCommandsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var databaseLease =
            await DatabaseTransactionGate.EnterAsync(_database, cancellationToken);
        var pending = (int)AgvCommandStatus.Pending;
        var retry = (int)AgvCommandStatus.RetryScheduled;
        var localNow = now.LocalDateTime;
        var entities = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item =>
                (item.Status == pending || item.Status == retry) &&
                item.NextAttemptAt <= localNow)
            .OrderBy(item => item.NextAttemptAt)
            .OrderBy(item => item.CreatedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        return entities.Select(ToItem).ToArray();
    }

    public async Task<AgvCommandOutboxItem?> TryClaimAsync(
        string commandId,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = (int)AgvCommandStatus.Pending;
        var retry = (int)AgvCommandStatus.RetryScheduled;
        var now = claimedAt.LocalDateTime;
        var claimedThisCall = false;
        Func<ISqlSugarClient, Task<DbResult<bool>>> transactionBody = database => database.Ado.UseTranAsync(async () =>
        {
            var affected = await database.Updateable<AgvCommandOutboxEntity>()
                .SetColumns(item => new AgvCommandOutboxEntity
                {
                    Status = (int)AgvCommandStatus.Sending,
                    AttemptCount = item.AttemptCount + 1,
                    ClaimedAt = now,
                    UpdatedAt = now
                })
                .Where(item =>
                    item.CommandId == commandId &&
                    (item.Status == pending || item.Status == retry) &&
                    item.NextAttemptAt <= now)
                .ExecuteCommandAsync(cancellationToken);
            if (affected != 1)
            {
                return;
            }

            claimedThisCall = true;

            var command = await database.Queryable<AgvCommandOutboxEntity>()
                .Where(item => item.CommandId == commandId)
                .FirstAsync(cancellationToken);
            if (command is not null)
            {
                await database.Updateable<DispatchTaskStepEntity>()
                    .SetColumns(step => new DispatchTaskStepEntity
                    {
                        Status = (int)DispatchTaskStepStatus.Sending,
                        RetryCount = command.AttemptCount - 1,
                        UpdatedAt = now,
                        ErrorMessage = null
                    })
                    .Where(step => step.StepId == command.StepId)
                    .ExecuteCommandAsync(cancellationToken);
            }
        });
        var transaction = await DatabaseTransactionGate.RunIsolatedAsync(_database, transactionBody, cancellationToken);
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"领取AGV命令{commandId}失败。",
                transaction.ErrorException);
        }

        if (!claimedThisCall) return null;

        var claimed = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item =>
                item.CommandId == commandId &&
                item.Status == (int)AgvCommandStatus.Sending &&
                item.ClaimedAt == now)
            .FirstAsync(cancellationToken);
        return claimed is null ? null : ToItem(claimed);
    }

    public async Task MarkSentAsync(
        string commandId,
        string vendorCommandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorCommandId);
        cancellationToken.ThrowIfCancellationRequested();
        await using var databaseLease =
            await DatabaseTransactionGate.EnterAsync(_database, cancellationToken);
        Func<ISqlSugarClient, Task<DbResult<bool>>> transactionBody = database => database.Ado.UseTranAsync(async () =>
        {
            var command = await database.Queryable<AgvCommandOutboxEntity>()
                .Where(item => item.CommandId == commandId)
                .FirstAsync(cancellationToken) ??
                throw new KeyNotFoundException($"找不到AGV命令{commandId}。");
            if (command.Status == (int)AgvCommandStatus.Sent)
            {
                return;
            }

            if (command.Status != (int)AgvCommandStatus.Sending)
            {
                throw new InvalidOperationException($"AGV命令{commandId}不在发送中状态。");
            }
            var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(command.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("AGV运输命令负载为空。");

            var step = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item => item.StepId == command.StepId)
                .FirstAsync(cancellationToken) ??
                throw new KeyNotFoundException($"找不到AGV命令{commandId}对应的任务步骤。");
            var targetStage = ((DispatchTaskStepType)step.StepType) switch
            {
                DispatchTaskStepType.GoToPickup => DispatchTaskStage.GoingToPickup,
                DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.GoingToDumper,
                DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
                DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.ReturningEmptyBox,
                _ => throw new InvalidOperationException(
                    $"步骤{step.StepId}不是可发送的AGV运输步骤。")
            };

            var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
            var commandAffected = await database.Updateable<AgvCommandOutboxEntity>()
                .SetColumns(item => new AgvCommandOutboxEntity
                {
                    Status = (int)AgvCommandStatus.Sent,
                    VendorCommandId = vendorCommandId,
                    SentAt = now,
                    UpdatedAt = now,
                    LastError = null
                })
                .Where(item => item.CommandId == commandId && item.Status == (int)AgvCommandStatus.Sending)
                .ExecuteCommandAsync(cancellationToken);
            var stepAffected = await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(step => new DispatchTaskStepEntity
                {
                    Status = (int)DispatchTaskStepStatus.Executing,
                    VendorTaskId = vendorCommandId,
                    VehicleId = command.VehicleId,
                    StartedAt = step.StartedAt ?? now,
                    UpdatedAt = now,
                    ErrorMessage = null
                })
                .Where(step => step.StepId == command.StepId)
                .ExecuteCommandAsync(cancellationToken);
            var taskAffected = payload.WindowIndex > 0
                ? await database.Updateable<DispatchTaskEntity>()
                    .SetColumns(task => new DispatchTaskEntity
                    {
                        VendorTaskId = vendorCommandId,
                        VehicleId = command.VehicleId,
                        UpdatedAt = now,
                        ErrorMessage = null
                    })
                    .Where(task =>
                        task.TaskId == command.TaskId &&
                        task.VendorTaskId == payload.PreviousVendorTaskId &&
                        task.VehicleId == command.VehicleId &&
                        task.Stage != (int)DispatchTaskStage.Completed &&
                        task.Stage != (int)DispatchTaskStage.Cancelled &&
                        task.Stage != (int)DispatchTaskStage.Faulted)
                    .ExecuteCommandAsync(cancellationToken)
                : await database.Updateable<DispatchTaskEntity>()
                    .SetColumns(task => new DispatchTaskEntity
                    {
                        Stage = (int)targetStage,
                        VendorTaskId = vendorCommandId,
                        VehicleId = command.VehicleId,
                        UpdatedAt = now,
                        ErrorMessage = null
                    })
                    .Where(task =>
                        task.TaskId == command.TaskId &&
                        task.Stage == (int)DispatchTaskStage.SubmittingToAgv)
                    .ExecuteCommandAsync(cancellationToken);
            if (commandAffected != 1 || stepAffected != 1 || taskAffected != 1)
            {
                throw new InvalidOperationException(
                    $"AGV命令{commandId}关联的任务或步骤状态已被并发修改。");
            }
        });
        var transaction = await DatabaseTransactionGate.RunIsolatedAsync(_database, transactionBody, cancellationToken);
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"确认AGV命令{commandId}发送成功时落库失败。",
                transaction.ErrorException);
        }
    }

    public async Task ScheduleRetryAsync(
        string commandId,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var databaseLease =
            await DatabaseTransactionGate.EnterAsync(_database, cancellationToken);
        Func<ISqlSugarClient, Task<DbResult<bool>>> transactionBody = database => database.Ado.UseTranAsync(async () =>
        {
            var command = await database.Queryable<AgvCommandOutboxEntity>()
                .Where(item => item.CommandId == commandId)
                .FirstAsync(cancellationToken) ??
                throw new KeyNotFoundException($"找不到AGV命令{commandId}。");
            if (command.Status != (int)AgvCommandStatus.Sending)
            {
                return;
            }

            var failed = command.AttemptCount >= maximumAttempts;
            var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
            var message = Truncate(errorMessage);
            await database.Updateable<AgvCommandOutboxEntity>()
                .SetColumns(item => new AgvCommandOutboxEntity
                {
                    Status = failed
                        ? (int)AgvCommandStatus.Failed
                        : (int)AgvCommandStatus.RetryScheduled,
                    NextAttemptAt = nextAttemptAt.LocalDateTime,
                    ClaimedAt = null,
                    UpdatedAt = now,
                    LastError = message
                })
                .Where(item => item.CommandId == commandId && item.Status == (int)AgvCommandStatus.Sending)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(step => new DispatchTaskStepEntity
                {
                    Status = failed
                        ? (int)DispatchTaskStepStatus.Failed
                        : (int)DispatchTaskStepStatus.RetryScheduled,
                    RetryCount = command.AttemptCount,
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(step => step.StepId == command.StepId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<DispatchTaskEntity>()
                .SetColumns(task => new DispatchTaskEntity
                {
                    Stage = failed ? (int)DispatchTaskStage.Faulted : task.Stage,
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(task => task.TaskId == command.TaskId)
                .ExecuteCommandAsync(cancellationToken);
        });
        var transaction = await DatabaseTransactionGate.RunIsolatedAsync(_database, transactionBody, cancellationToken);
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"更新AGV命令{commandId}重试状态失败。",
                transaction.ErrorException);
        }
    }

    public async Task DeferBeforeSendAsync(
        string commandId,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var databaseLease =
            await DatabaseTransactionGate.EnterAsync(_database, cancellationToken);
        Func<ISqlSugarClient, Task<DbResult<bool>>> transactionBody = database => database.Ado.UseTranAsync(async () =>
        {
            var command = await database.Queryable<AgvCommandOutboxEntity>()
                .Where(item => item.CommandId == commandId)
                .FirstAsync(cancellationToken) ??
                throw new KeyNotFoundException($"找不到AGV命令{commandId}。");
            if (command.Status != (int)AgvCommandStatus.Sending)
            {
                return;
            }

            var previousAttempts = Math.Max(0, command.AttemptCount - 1);
            var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
            var message = Truncate(errorMessage);
            await database.Updateable<AgvCommandOutboxEntity>()
                .SetColumns(item => new AgvCommandOutboxEntity
                {
                    Status = (int)AgvCommandStatus.RetryScheduled,
                    AttemptCount = previousAttempts,
                    NextAttemptAt = nextAttemptAt.LocalDateTime,
                    ClaimedAt = null,
                    UpdatedAt = now,
                    LastError = message
                })
                .Where(item => item.CommandId == commandId && item.Status == (int)AgvCommandStatus.Sending)
                .ExecuteCommandAsync(cancellationToken);
            await database.Updateable<DispatchTaskStepEntity>()
                .SetColumns(step => new DispatchTaskStepEntity
                {
                    Status = (int)DispatchTaskStepStatus.RetryScheduled,
                    RetryCount = previousAttempts,
                    UpdatedAt = now,
                    ErrorMessage = message
                })
                .Where(step => step.StepId == command.StepId)
                .ExecuteCommandAsync(cancellationToken);
        });
        var transaction = await DatabaseTransactionGate.RunIsolatedAsync(_database, transactionBody, cancellationToken);
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException(
                $"回滚AGV命令{commandId}的发送前临时失败失败。",
                transaction.ErrorException);
        }
    }

    public async Task<int> RecoverStaleClaimsAsync(
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var databaseLease =
            await DatabaseTransactionGate.EnterAsync(_database, cancellationToken);
        var stale = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item =>
                item.Status == (int)AgvCommandStatus.Sending &&
                item.ClaimedAt != null &&
                item.ClaimedAt <= staleBefore.LocalDateTime)
            .ToListAsync(cancellationToken);
        var recovered = 0;
        foreach (var command in stale)
        {
            Func<ISqlSugarClient, Task<DbResult<bool>>> transactionBody = database => database.Ado.UseTranAsync(async () =>
            {
                var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
                var affected = await database.Updateable<AgvCommandOutboxEntity>()
                    .SetColumns(item => new AgvCommandOutboxEntity
                    {
                        Status = (int)AgvCommandStatus.RetryScheduled,
                        NextAttemptAt = now,
                        ClaimedAt = null,
                        UpdatedAt = now,
                        LastError = "服务重启或发送租约超时，命令已回收等待重试。"
                    })
                    .Where(item =>
                        item.CommandId == command.CommandId &&
                        item.Status == (int)AgvCommandStatus.Sending &&
                        item.ClaimedAt == command.ClaimedAt && item.ClaimedAt <= staleBefore.LocalDateTime)
                    .ExecuteCommandAsync(cancellationToken);
                if (affected == 1)
                {
                    await database.Updateable<DispatchTaskStepEntity>()
                        .SetColumns(step => new DispatchTaskStepEntity
                        {
                            Status = (int)DispatchTaskStepStatus.RetryScheduled,
                            UpdatedAt = now,
                            ErrorMessage = "发送租约超时，等待恢复重试。"
                        })
                        .Where(step => step.StepId == command.StepId)
                        .ExecuteCommandAsync(cancellationToken);
                    recovered++;
                }
            });
            var transaction = await DatabaseTransactionGate.RunIsolatedAsync(_database, transactionBody, cancellationToken);
            if (!transaction.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"回收AGV命令{command.CommandId}失败。",
                    transaction.ErrorException);
            }
        }

        return recovered;
    }

    private static AgvCommandOutboxItem ToItem(AgvCommandOutboxEntity item) => new(
        item.CommandId,
        item.IdempotencyKey,
        item.TaskId,
        item.StepId,
        item.VehicleId,
        (AgvCommandType)item.CommandType,
        (AgvCommandStatus)item.Status,
        item.PayloadJson,
        item.AttemptCount,
        ToOffset(item.NextAttemptAt),
        ToOffset(item.CreatedAt),
        ToOffset(item.UpdatedAt),
        ToOffset(item.ClaimedAt),
        ToOffset(item.SentAt),
        item.VendorCommandId,
        item.LastError);

    private static string Truncate(string value) =>
        value.Length <= 1000 ? value : value[..1000];

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Local));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value.HasValue ? ToOffset(value.Value) : null;
}
