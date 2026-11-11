using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

/// <summary>
/// Persistent conservative traffic locks. ExpiresAt is a diagnostic/heartbeat timestamp;
/// locks are never automatically released solely because time elapsed.
/// </summary>
public sealed partial class TrafficResourceManager(
    ISqlSugarClient database,
    TrafficResourceCatalog catalog,
    TrafficControlSettings settings, TimeProvider? clock = null) : ITrafficResourceManager, ITaskWaitingBayAllocator
{
    public Task PrepareDispatchCycleAsync(CancellationToken cancellationToken) => PruneCompletedTasksAsync(cancellationToken);
    public async Task<IReadOnlySet<string>> GetHeldTaskIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await database.Queryable<TrafficResourceLeaseEntity>().Select(l => l.TaskId)
            .Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> GetHeldVehicleIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await database.Queryable<TrafficResourceLeaseEntity>().Select(l => l.VehicleId)
            .Distinct().ToListAsync(cancellationToken)).Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> GetUnavailableResourceIdsAsync(
        string requestingTaskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingTaskId);
        cancellationToken.ThrowIfCancellationRequested();
        var leases = await database.Queryable<TrafficResourceLeaseEntity>()
            .Where(lease => lease.TaskId != requestingTaskId)
            .ToListAsync(cancellationToken);
        return leases
            .GroupBy(lease => lease.ResourceId, StringComparer.OrdinalIgnoreCase)
            .Where(group =>
                catalog.TryGet(group.Key, out var resource) &&
                group.Select(lease => lease.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() >=
                resource.Capacity)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task<TrafficReservationResult> TryAcquireAsync(
        string ownerStepId,
        string taskId,
        string vehicleId,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken) => AcquireCoreAsync(ownerStepId, taskId, vehicleId, resourceIds, cancellationToken);

    public async Task<TaskWaitingBayBinding?> GetAsync(string taskId, CancellationToken ct)
    {
        var row = await database.Queryable<TaskWaitingBayEntity>().Where(b => b.TaskId == taskId).FirstAsync(ct);
        return row is null ? null : new(row.TaskId,row.VehicleId,row.StepId,row.MapId,row.MapVersionId,row.WaitingSite,row.ResourceId);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetSelectionCountsAsync(string mapId, string versionId, CancellationToken ct)
    {
        var rows = await database.Queryable<TaskWaitingBayEntity>()
            .Where(b => b.MapId == mapId && b.MapVersionId == versionId)
            .GroupBy(b => b.WaitingSite)
            .Select(b => new { Site = b.WaitingSite, Count = SqlFunc.AggregateCount(b.TaskId) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Site, r => r.Count, StringComparer.OrdinalIgnoreCase);
    }

    public Task<TrafficReservationResult> TryAcquireAsync(TaskWaitingBayBinding binding,
        IReadOnlyCollection<string> routeResources, CancellationToken ct)
    {
        if (new[] { binding.TaskId,binding.StepId,binding.VehicleId,binding.MapId,binding.MapVersionId,binding.WaitingSite,binding.ResourceId }.Any(string.IsNullOrWhiteSpace) ||
            !routeResources.Contains(binding.ResourceId,StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("候车位绑定及独立资源不完整。");
        return AcquireCoreAsync(binding.StepId,binding.TaskId,binding.VehicleId,routeResources,ct,binding);
    }

    private async Task<TrafficReservationResult> AcquireCoreAsync(string ownerStepId,string taskId,string vehicleId,
        IReadOnlyCollection<string> resourceIds,CancellationToken cancellationToken,TaskWaitingBayBinding? binding=null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        ArgumentNullException.ThrowIfNull(resourceIds);
        cancellationToken.ThrowIfCancellationRequested();
        await PruneCompletedTasksAsync(cancellationToken);

        var requested = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var resourceId in requested)
        {
            if (!catalog.TryGet(resourceId, out var resource) || resource.Capacity < 1)
            {
                throw new InvalidOperationException($"交通资源{resourceId}未配置或容量无效。");
            }
        }

        if (requested.Length == 0)
        {
            var emptyResult = await database.Ado.UseTranAsync(async () => await WriteStateAsync(ownerStepId, taskId, vehicleId,
                "NotRequired", "本路径无需申请交通资源。", [], cancellationToken));
            if (!emptyResult.IsSuccess) throw new InvalidOperationException("保存无资源路径状态失败。", emptyResult.ErrorException);
            return new TrafficReservationResult(true);
        }

        string? blockerResource = null;
        string? blockerTask = null;
        var now = DateTime.Now;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var context = await database.Queryable<TrafficRequestEntity>().Where(r => r.StepId == ownerStepId).FirstAsync(cancellationToken);
            // Legacy system callers also join the ordinary priority/FIFO queue.
            // An actual acquisition attempt refreshes a proposal, never its FIFO
            // start and never an already granted physical lease.
            if (context is null)
            {
                await WriteStateAsync(ownerStepId, taskId, vehicleId, "Requested", "申请交通资源，按普通优先级和首次请求时间排队。", requested, cancellationToken);
                context = await database.Queryable<TrafficRequestEntity>().Where(r => r.StepId == ownerStepId).FirstAsync(cancellationToken);
            }
            if (context is not null)
            {
                if (context.TaskId != taskId || context.VehicleId != vehicleId)
                    throw new InvalidOperationException("交通排队请求的步骤、任务或车辆身份不一致。");
                context.QueueObservedAtUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
                await database.Updateable<TrafficRequestEntity>()
                    .SetColumns(r => r.QueueObservedAtUtc == context.QueueObservedAtUtc)
                    .Where(r => r.StepId == ownerStepId).ExecuteCommandAsync(cancellationToken);
            }
            if(binding is not null)
            {
                var selected=await GetAsync(taskId,cancellationToken);
                if(selected is not null && selected!=binding) throw new InvalidOperationException("原任务候车位已绑定，禁止在重试或恢复时更换位置。");
                var step=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.StepId==ownerStepId&&s.TaskId==taskId).FirstAsync(cancellationToken);
                var task=await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==taskId).FirstAsync(cancellationToken);
                if(context is null || context.MapId!=binding.MapId || context.MapVersionId!=binding.MapVersionId ||
                    step is null || step.StepType!=(int)DispatchTaskStepType.DeliverFullBoxToDumper || step.Status!=(int)DispatchTaskStepStatus.Ready ||
                    task is null || task.VehicleId!=vehicleId || task.Stage!=(int)DispatchTaskStage.PickingUp)
                    throw new InvalidOperationException("候车选位的原任务、车辆、送箱步骤或地图版本发生变化。");
            }
            foreach (var resourceId in requested)
            {
                var resource = catalog.TryGet(resourceId, out var definition)
                    ? definition
                    : throw new InvalidOperationException($"交通资源{resourceId}不存在。");
                var leases = await database.Queryable<TrafficResourceLeaseEntity>()
                    .Where(lease => lease.ResourceId == resourceId)
                    .ToListAsync(cancellationToken);
                var otherTasks = leases
                    .Where(lease => !string.Equals(
                        lease.TaskId,
                        taskId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(lease => lease.TaskId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (otherTasks.Length >= resource.Capacity)
                {
                    blockerResource = resourceId;
                    blockerTask = otherTasks[0];
                    await WriteStateAsync(ownerStepId, taskId, vehicleId, "Waiting", $"资源{resourceId}容量已满，等待任务{blockerTask}释放。", requested, cancellationToken);
                    return;
                }
                // A capacity becoming free is not a permit to jump ahead of a
                // freshly evaluated higher-priority/older waiting call. Existing
                // owners always keep their resource, regardless of queue order.
                if (!leases.Any(l => string.Equals(l.TaskId, taskId, StringComparison.OrdinalIgnoreCase)) &&
                    await FindQueuePredecessorAsync(context, taskId, resourceId,
                        resource.Capacity - otherTasks.Length, cancellationToken) is { } predecessor)
                {
                    blockerResource = resourceId;
                    blockerTask = predecessor;
                    await WriteStateAsync(ownerStepId, taskId, vehicleId, "Waiting",
                        $"资源{resourceId}按有效优先级和呼叫时间排队，等待前序任务{predecessor}申请；已有路权保持不变。",
                        requested, cancellationToken);
                    return;
                }
            }

            var newlyAcquired = new List<string>();
            foreach (var resourceId in requested)
            {
                var exists = await database.Queryable<TrafficResourceLeaseEntity>()
                    .AnyAsync(lease =>
                        lease.ResourceId == resourceId &&
                        lease.OwnerStepId == ownerStepId,
                        cancellationToken);
                if (exists)
                {
                    var owner = await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.ResourceId == resourceId && l.OwnerStepId == ownerStepId).FirstAsync(cancellationToken);
                    if (owner.TaskId != taskId || owner.VehicleId != vehicleId) throw new InvalidOperationException("已有路权步骤与任务或车辆归属冲突。");
                    if (owner.MapId != null && context?.MapId != null && (owner.MapId != context.MapId || owner.MapVersionId != context.MapVersionId))
                        throw new InvalidOperationException("已有路权属于另一地图版本，不能复用为新版本路权。");
                    await database.Updateable<TrafficResourceLeaseEntity>()
                        .SetColumns(lease => new TrafficResourceLeaseEntity
                        {
                            RenewedAt = now,
                            ExpiresAt = now.Add(settings.LeaseDuration)
                        })
                        .Where(lease =>
                            lease.ResourceId == resourceId &&
                            lease.OwnerStepId == ownerStepId)
                        .ExecuteCommandAsync(cancellationToken);
                    continue;
                }

                await database.Insertable(new TrafficResourceLeaseEntity
                {
                    LeaseId = $"TRL-{Guid.NewGuid():N}",
                    ResourceId = resourceId,
                    OwnerStepId = ownerStepId,
                    TaskId = taskId,
                    VehicleId = vehicleId,
                    MapId = context?.MapId, MapVersionId = context?.MapVersionId,
                    AcquiredAt = now,
                    RenewedAt = now,
                    ExpiresAt = now.Add(settings.LeaseDuration)
                }).ExecuteCommandAsync(cancellationToken);
                newlyAcquired.Add(resourceId);
            }
            if (newlyAcquired.Count > 0)
                await RecordLeaseChangeAsync(ownerStepId, taskId, vehicleId, "ResourceAcquired", newlyAcquired,
                    "实际取得所列资源；原占用已在本事务中核对，后续按车体与交接证据释放。", cancellationToken);
            if(binding is not null && await GetAsync(taskId,cancellationToken) is null)
                await database.Insertable(new TaskWaitingBayEntity{TaskId=taskId,VehicleId=vehicleId,StepId=ownerStepId,
                    MapId=binding.MapId,MapVersionId=binding.MapVersionId,WaitingSite=binding.WaitingSite,ResourceId=binding.ResourceId,
                    SelectedAt=now}).ExecuteCommandAsync(cancellationToken);
            var previous = await database.Queryable<TrafficRequestEntity>().Where(r => r.StepId == ownerStepId).FirstAsync(cancellationToken);
            await WriteStateAsync(ownerStepId, taskId, vehicleId, previous?.State == "Held" ? "Held" : "Acquired", "已取得路权；心跳过期仍保留占用，等待正常交接或任务完成。", requested, cancellationToken);
        });

        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException("申请交通资源事务失败。", transaction.ErrorException);
        }

        return blockerResource is null
            ? new TrafficReservationResult(true)
            : new TrafficReservationResult(false, blockerResource, blockerTask);
    }

    public Task ReleaseStepAsync(string ownerStepId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerStepId); ct.ThrowIfCancellationRequested();
        return ReleaseAsync(null, ownerStepId, false, "运输命令未准备成功，回滚本步骤申请的路权。", ct);
    }
    public async Task<int> ReleaseStepExceptResourcesAsync(string ownerStepId,
        IReadOnlyCollection<string> keepResourceIds, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerStepId);
        ArgumentNullException.ThrowIfNull(keepResourceIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();
        var keep = keepResourceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var released = 0;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var leases = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(lease => lease.OwnerStepId == ownerStepId).ToListAsync(ct);
            var remove = leases.Where(lease => !keep.Contains(lease.ResourceId)).ToArray();
            if (remove.Length == 0) return;
            await database.Deleteable<TrafficResourceLeaseEntity>()
                .Where(lease => remove.Select(item => item.LeaseId).Contains(lease.LeaseId))
                .ExecuteCommandAsync(ct);
            var remaining = leases.Except(remove).ToArray();
            var first = leases[0];
            await RecordLeaseChangeAsync(ownerStepId, first.TaskId, first.VehicleId, "ResourceReleased",
                remove.Select(l => l.ResourceId), reason, ct);
            await WriteStateAsync(ownerStepId, first.TaskId, first.VehicleId,
                remaining.Length == 0 ? "Released" : "Held", reason,
                remaining.Select(item => item.ResourceId).ToArray(), ct);
            released = remove.Length;
        });
        if (!transaction.IsSuccess)
            throw new InvalidOperationException("滚动路权窗口交接释放失败，原占用保持不变。", transaction.ErrorException);
        return released;
    }
    public Task ReleaseTaskExceptStepAsync(string taskId, string ownerStepId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); ArgumentException.ThrowIfNullOrWhiteSpace(ownerStepId); ct.ThrowIfCancellationRequested();
        return ReleaseAsync(taskId, ownerStepId, true, "新运输步骤已取得路权并写入命令，释放前序步骤路权。", ct);
    }
    public Task ReleaseTaskAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); ct.ThrowIfCancellationRequested();
        return ReleaseAsync(taskId, null, false, "业务任务完成，正常释放任务持有路权。", ct);
    }

    /// <summary>
    /// Releases only named leases from one completed transport step. The caller
    /// must first prove that the vehicle has reached the step destination and
    /// that its complete safety envelope is outside every released resource.
    /// </summary>
    public async Task<int> ReleasePassedLeasesAsync(string taskId, string stepId,
        IReadOnlyCollection<string> leaseIds, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(leaseIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();
        var ids = leaseIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return 0;

        var released = 0;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var leases = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(l => l.TaskId == taskId && l.OwnerStepId == stepId && ids.Contains(l.LeaseId))
                .ToListAsync(ct);
            if (leases.Count == 0) return;
            await database.Deleteable<TrafficResourceLeaseEntity>()
                .Where(l => l.TaskId == taskId && l.OwnerStepId == stepId &&
                    leases.Select(item => item.LeaseId).Contains(l.LeaseId))
                .ExecuteCommandAsync(ct);
            var remaining = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(l => l.TaskId == taskId && l.OwnerStepId == stepId)
                .ToListAsync(ct);
            var first = leases[0];
            await RecordLeaseChangeAsync(stepId, taskId, first.VehicleId, "ResourceReleased",
                leases.Select(l => l.ResourceId), reason, ct);
            await WriteStateAsync(stepId, taskId, first.VehicleId,
                remaining.Count == 0 ? "Released" : "Held",
                remaining.Count == 0
                    ? reason
                    : reason + " 终点或仍有车体覆盖的资源继续占用。",
                remaining.Select(l => l.ResourceId).ToArray(), ct);
            released = leases.Count;
        });
        if (!transaction.IsSuccess)
            throw new InvalidOperationException("已完成运输路段释放事务失败，原占用保持不变。",
                transaction.ErrorException);
        return released;
    }

    public async Task RenewTaskAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); ct.ThrowIfCancellationRequested();
        var now = DateTime.Now;
        var result = await database.Ado.UseTranAsync(async () =>
        {
            var leases = await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == taskId).ToListAsync(ct);
            await database.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => new TrafficResourceLeaseEntity
                { RenewedAt = now, ExpiresAt = now.Add(settings.LeaseDuration) }).Where(l => l.TaskId == taskId).ExecuteCommandAsync(ct);
            foreach (var group in leases.GroupBy(l => l.OwnerStepId))
            {
                var request = await database.Queryable<TrafficRequestEntity>().Where(r => r.StepId == group.Key).FirstAsync(ct);
                // Holding some resources while waiting for more must not restart the waiting episode every heartbeat.
                if (request?.State == "Waiting") continue;
                await WriteStateAsync(group.Key, taskId, group.First().VehicleId, "Held", "已取得路权；心跳过期仍保留占用，等待正常交接或任务完成。", group.Select(l => l.ResourceId).ToArray(), ct);
            }
        });
        if (!result.IsSuccess) throw new InvalidOperationException("路权续期及保持状态保存失败。", result.ErrorException);
    }

    private async Task PruneCompletedTasksAsync(CancellationToken cancellationToken)
    {
        var heldTaskIds = await database.Queryable<TrafficResourceLeaseEntity>().Select(l => l.TaskId).Distinct().ToListAsync(cancellationToken);
        if (heldTaskIds.Count == 0) return;
        var completed = (int)DispatchTaskStage.Completed;
        var completedTaskIds = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.Stage == completed && heldTaskIds.Contains(task.TaskId))
            .Select(task => task.TaskId)
            .ToListAsync(cancellationToken);
        if (completedTaskIds.Count > 0)
        {
            foreach (var taskId in completedTaskIds) await ReleaseTaskAsync(taskId, cancellationToken);
        }
    }
}
