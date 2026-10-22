using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceManager
{
    public async Task RecordRouteAsync(TrafficRouteObservation route, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (route.WaitingCategory is not ("Route" or "Equipment")) throw new ArgumentException("等待类别无效。");
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
        var ids = JsonSerializer.Serialize(resources.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var edges = route == null ? old?.EdgeIds ?? "[]" : JsonSerializer.Serialize(route.EdgeIds);
        // Replanning while still waiting does not constitute a grant or end the episode.
        if (state == "Requested" && old?.State == "Waiting") { state = "Waiting"; reason = old.Reason; }
        // Keep polling idempotent, but a newly chosen route must replace the old diagnostic route.
        if (state == "Requested" && old?.State is ("Acquired" or "Held" or "Waiting") && old.VehicleId == vehicle &&
            old.ResourceIds == ids && old.EdgeIds == edges && (route == null || old.Source == route.Source && old.Destination == route.Destination && old.MapId == route.MapId && old.MapVersionId == route.MapVersionId)) return;
        var changed = old == null || old.State != state || old.Reason != reason || old.VehicleId != vehicle || old.ResourceIds != ids || old.EdgeIds != edges || route != null && (old.MapId != route.MapId || old.MapVersionId != route.MapVersionId || old.Source != route.Source || old.Destination != route.Destination || old.WaitingCategory != route.WaitingCategory);
        var previousWait = old?.WaitingSinceUtc;
        var row = old ?? new TrafficRequestEntity { StepId = step, TaskId = task, RequestedAt = DateTime.Now };
        var contextChanged = old != null && (old.VehicleId != vehicle || route != null &&
            (old.MapId != route.MapId || old.MapVersionId != route.MapVersionId || old.Source != route.Source || old.Destination != route.Destination));
        var previousBlocking = row.BlockingKey;
        var wasWaiting = old?.State == "Waiting";
        if (state == "Waiting")
        {
            if (old?.State != "Waiting" || contextChanged)
            { row.WaitingSinceUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime; row.WaitingStartKnown = true; }
            else if (row.WaitingSinceUtc == null)
            { row.WaitingSinceUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime; row.WaitingStartKnown = false; }
        }
        else { row.WaitingSinceUtc = null; row.WaitingStartKnown = null; }
        changed |= previousWait != row.WaitingSinceUtc;
        var activeBlockers = state == "Waiting" ? TrafficBlockageObservation.Find(resources,task,
            await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId!=task).ToListAsync(ct),catalog) : [];
        var blockingKey = TrafficBlockageObservation.Key(activeBlockers);
        string? blockingEvent = null;
        if (blockingKey != "[]")
        {
            if (previousBlocking is null or "[]" || contextChanged)
            {
                row.BlockingSinceUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
                row.BlockingStartKnown = previousBlocking != null || !wasWaiting || contextChanged;
                blockingEvent = "BlockingStarted";
            }
            else if (previousBlocking != blockingKey) blockingEvent = "BlockersChanged";
        }
        else if (previousBlocking is not (null or "[]"))
        {
            row.LastBlockingClearedUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
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
            OccurredAt=(clock ?? TimeProvider.System).GetUtcNow().LocalDateTime
        }).ExecuteCommandAsync(ct);
        row.VehicleId = vehicle; row.State = state; row.Reason = reason; row.ResourceIds = ids; row.EdgeIds = edges; row.UpdatedAt = DateTime.Now;
        if (route != null) { row.Source = route.Source; row.Destination = route.Destination; row.MapId = route.MapId; row.MapVersionId = route.MapVersionId; row.WaitingCategory = route.WaitingCategory; }
        if (old == null) await database.Insertable(row).ExecuteCommandAsync(ct);
        else await database.Updateable(row).ExecuteCommandAsync(ct);
        if (changed) await database.Insertable(new TrafficLifecycleEventEntity
        {
            EventId = Guid.NewGuid().ToString("N"), StepId = step, TaskId = task, VehicleId = vehicle,
            Kind = state, Reason = reason, ResourceIds = ids, WaitingSinceUtc = row.WaitingSinceUtc, WaitingStartKnown = row.WaitingStartKnown, OccurredAt = DateTime.Now
        }).ExecuteCommandAsync(ct);
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
                await WriteStateAsync(lease.OwnerStepId, lease.TaskId, lease.VehicleId, "Released", reason, group.Select(l => l.ResourceId).ToArray(), ct);
            }
        });
        if (!result.IsSuccess) throw new InvalidOperationException("释放路权及过程记录事务失败。", result.ErrorException);
    }
}
