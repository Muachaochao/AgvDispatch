using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceManager
{
    private Task<int> RecordLeaseChangeAsync(string step, string task, string vehicle, string kind,
        IEnumerable<string> ids, string reason, CancellationToken ct) =>
        database.Insertable(new TrafficLifecycleEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"), StepId = step, TaskId = task, VehicleId = vehicle,
            Kind = kind, Reason = reason,
            ResourceIds = JsonSerializer.Serialize(ids.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
            OccurredAt = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().LocalDateTime
        }).ExecuteCommandAsync(ct);

    public async Task RecordRouteAsync(TrafficRouteObservation route, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (route.WaitingCategory is not ("Route" or "Equipment" or "ReferenceRoute")) throw new ArgumentException("等待类别无效。");
        if (route.WaitingCategory == "ReferenceRoute")
            route = route with { Waiting = true, QueueEligible = false, QueuePriority = null };
        if (route.QueuePriority is { } priority && !double.IsFinite(priority)) throw new ArgumentException("路权排队优先级必须为有限数值。");
        var result = await database.Ado.UseTranAsync(async () =>
            await WriteStateAsync(route.StepId, route.TaskId, route.VehicleId, route.Waiting ? "Waiting" :
                route.WaitingCategory == "Equipment" ? "ObservedReady" : "Requested", route.Reason, route.ResourceIds, ct, route));
        if (!result.IsSuccess) throw new InvalidOperationException("记录路权路径依据失败。", result.ErrorException);
    }
    private async Task WriteStateAsync(string step, string task, string vehicle, string state, string reason,
        IReadOnlyCollection<string> resources, CancellationToken ct, TrafficRouteObservation? route = null)
    {
        var old = await database.Queryable<TrafficRequestEntity>().Where(r => r.StepId == step).FirstAsync(ct);
        if (old != null && old.TaskId != task) throw new InvalidOperationException("路权请求步骤与业务任务归属冲突。");
        if (route is { QueueEligible: false } && old?.State is ("Acquired" or "Held"))
        {
            // Withdrawing a planning proposal must not rewrite a granted route
            // or demote its physical ownership to a waiting diagnostic.
            await database.Updateable<TrafficRequestEntity>()
                .SetColumns(r => r.QueueObservedAtUtc == null).Where(r => r.StepId == step).ExecuteCommandAsync(ct);
            return;
        }
        var ids = JsonSerializer.Serialize(resources.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var edges = route == null ? old?.EdgeIds ?? "[]" : JsonSerializer.Serialize(route.EdgeIds);
        // Replanning while still waiting does not constitute a grant or end the episode.
        if (state == "Requested" && old?.State == "Waiting") { state = "Waiting"; reason = old.Reason; }
        // Keep polling idempotent, but a newly chosen route must replace the old diagnostic route.
        if (state == "Requested" && old?.State is ("Acquired" or "Held") && old.VehicleId == vehicle &&
            old.ResourceIds == ids && old.EdgeIds == edges && route?.QueuePriority == null && (route == null || route.QueueEligible && old.QueuePriority == null && old.Source == route.Source && old.Destination == route.Destination && old.MapId == route.MapId && old.MapVersionId == route.MapVersionId)) return;
        var changed = old == null || old.State != state || old.Reason != reason || old.VehicleId != vehicle || old.ResourceIds != ids || old.EdgeIds != edges || route != null && (old.MapId != route.MapId || old.MapVersionId != route.MapVersionId || old.Source != route.Source || old.Destination != route.Destination || old.WaitingCategory != route.WaitingCategory);
        var previousWait = old?.WaitingSinceUtc;
        var row = old ?? new TrafficRequestEntity { StepId = step, TaskId = task,
            RequestedAt = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().LocalDateTime };
        var contextChanged = old != null && (old.VehicleId != vehicle || route != null &&
            (old.MapId != route.MapId || old.MapVersionId != route.MapVersionId || old.Source != route.Source || old.Destination != route.Destination));
        var previousBlocking = row.BlockingKey;
        var wasWaiting = old?.State == "Waiting";
        if (state == "Waiting")
        {
            if (old?.State != "Waiting" || contextChanged)
            { row.WaitingSinceUtc = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().UtcDateTime; row.WaitingStartKnown = true; }
            else if (row.WaitingSinceUtc == null)
            { row.WaitingSinceUtc = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().UtcDateTime; row.WaitingStartKnown = false; }
        }
        else { row.WaitingSinceUtc = null; row.WaitingStartKnown = null; }
        changed |= previousWait != row.WaitingSinceUtc;
        var activeBlockers = state == "Waiting" && route?.WaitingCategory != "ReferenceRoute" ? TrafficBlockageObservation.Find(resources,task,
            await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId!=task).ToListAsync(ct),catalog) : [];
        var blockingKey = TrafficBlockageObservation.Key(activeBlockers);
        string? blockingEvent = null;
        if (blockingKey != "[]")
        {
            if (previousBlocking is null or "[]" || contextChanged)
            {
                row.BlockingSinceUtc = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().UtcDateTime;
                row.BlockingStartKnown = previousBlocking != null || !wasWaiting || contextChanged;
                blockingEvent = "BlockingStarted";
            }
            else if (previousBlocking != blockingKey) blockingEvent = "BlockersChanged";
        }
        else if (previousBlocking is not (null or "[]"))
        {
            row.LastBlockingClearedUtc = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().UtcDateTime;
            row.BlockingSinceUtc = null; row.BlockingStartKnown = null; blockingEvent = "BlockingCleared";
        }
        row.BlockingKey = blockingKey;
        changed |= previousBlocking != blockingKey;
        if (blockingEvent != null) await database.Insertable(new TrafficLifecycleEventEntity
        {
            EventId=Guid.NewGuid().ToString("N"),StepId=step,TaskId=task,VehicleId=vehicle,Kind=blockingEvent,
            Reason=blockingEvent=="BlockingCleared" ? "本请求的上次资源阻塞在本轮调度观察中解除；不代表车辆已离开，不自动授予路权。" :
                blockingEvent=="BlockersChanged" ? "持续阻塞期间占用方发生变化，保留本次阻塞起点。" :
                row.BlockingStartKnown==true ? "本轮调度首次观察到当前资源容量阻塞。" : "历史请求首次补记资源阻塞，起点仅为观察下界。",
            ResourceIds=blockingEvent=="BlockingCleared" ? JsonSerializer.Serialize((JsonSerializer.Deserialize<string[][]>(previousBlocking!)??[]).Select(a=>a[0]).Distinct(StringComparer.OrdinalIgnoreCase)) :
                JsonSerializer.Serialize(activeBlockers.Select(l=>l.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase)),
            OccurredAt=(clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().LocalDateTime
        }).ExecuteCommandAsync(ct);
        row.VehicleId = vehicle; row.State = state; row.Reason = reason; row.ResourceIds = ids; row.EdgeIds = edges; row.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        if (route is not null)
        {
            // This timestamp expires only an ungranted queue recommendation. It
            // never expires, revokes or releases a traffic lease.
            row.QueuePriority = route.QueuePriority;
            row.QueueObservedAtUtc = route.QueueEligible ? (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().UtcDateTime : null;
        }
        if (route != null) { row.Source = route.Source; row.Destination = route.Destination; row.MapId = route.MapId; row.MapVersionId = route.MapVersionId; row.WaitingCategory = route.WaitingCategory; }
        if (old == null) await database.Insertable(row).ExecuteCommandAsync(ct);
        else await database.Updateable(row).ExecuteCommandAsync(ct);
        if (changed) await database.Insertable(new TrafficLifecycleEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"), StepId = step, TaskId = task, VehicleId = vehicle,
            Kind = state, Reason = reason, ResourceIds = ids, WaitingSinceUtc = row.WaitingSinceUtc, WaitingStartKnown = row.WaitingStartKnown, OccurredAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime
        }).ExecuteCommandAsync(ct);
    }
    /// <summary>
    /// Releases only independent waiting-bay leases after the vehicle has physically
    /// arrived at the dumping station. The dumping resource and every other task
    /// lease remain owned until their normal closure.
    /// </summary>
    public async Task<int> ReleaseDepartedWaitingBaysAsync(
        string taskId,
        IReadOnlySet<string> waitingBayResources,
        string reason,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(waitingBayResources);
        if (waitingBayResources.Count == 0) return 0;
        ct.ThrowIfCancellationRequested();
        var released = 0;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var leases = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(l => l.TaskId == taskId && waitingBayResources.Contains(l.ResourceId))
                .ToListAsync(ct);
            if (leases.Count == 0) return;
            var ids = leases.Select(l => l.LeaseId).ToArray();
            await database.Deleteable<TrafficResourceLeaseEntity>()
                .Where(l => ids.Contains(l.LeaseId))
                .ExecuteCommandAsync(ct);
            foreach (var group in leases.GroupBy(l => l.OwnerStepId))
            {
                var lease = group.First();
                await RecordLeaseChangeAsync(group.Key, taskId, lease.VehicleId, "ResourceReleased",
                    group.Select(l => l.ResourceId), reason, ct);
                var remaining = await database.Queryable<TrafficResourceLeaseEntity>()
                    .Where(l => l.OwnerStepId == group.Key && l.TaskId == taskId)
                    .ToListAsync(ct);
                await WriteStateAsync(
                    group.Key,
                    taskId,
                    lease.VehicleId,
                    remaining.Count == 0 ? "Released" : "Held",
                    reason,
                    remaining.Select(l => l.ResourceId).ToArray(),
                    ct);
            }
            released = leases.Count;
        });
        if (!transaction.IsSuccess)
            throw new InvalidOperationException("候车位离位释放事务失败，原占用保持不变。", transaction.ErrorException);
        return released;
    }

    private async Task ReleaseAsync(string? task, string? step, bool except, string reason, CancellationToken ct)
    {
        var result = await database.Ado.UseTranAsync(async () =>
        {
            var query = database.Queryable<TrafficResourceLeaseEntity>();
            if (task != null) query = query.Where(l => l.TaskId == task);
            if (step != null) query = except ? query.Where(l => l.OwnerStepId != step) : query.Where(l => l.OwnerStepId == step);
            var leases = await query.ToListAsync(ct);
            if (leases.Count == 0) return;
            var ids = leases.Select(l => l.LeaseId).ToArray();
            await database.Deleteable<TrafficResourceLeaseEntity>().Where(l => ids.Contains(l.LeaseId)).ExecuteCommandAsync(ct);
            foreach (var group in leases.GroupBy(l => l.OwnerStepId))
            {
                var lease = group.First();
                await RecordLeaseChangeAsync(lease.OwnerStepId, lease.TaskId, lease.VehicleId, "ResourceReleased",
                    group.Select(l => l.ResourceId), reason, ct);
                await WriteStateAsync(lease.OwnerStepId, lease.TaskId, lease.VehicleId, "Released", reason, group.Select(l => l.ResourceId).ToArray(), ct);
            }
        });
        if (!result.IsSuccess) throw new InvalidOperationException("释放路权及过程记录事务失败。", result.ErrorException);
    }
}
