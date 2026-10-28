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
    public async Task<IReadOnlySet<string>> GetUnavailableResourceIdsAsync(
        string requestingTaskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingTaskId);
        cancellationToken.ThrowIfCancellationRequested();
        await PruneCompletedTasksAsync(cancellationToken);
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
            }

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
            }
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
