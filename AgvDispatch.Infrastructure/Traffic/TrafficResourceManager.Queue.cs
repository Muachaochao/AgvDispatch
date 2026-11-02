using System.Text.Json;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceManager
{
    private async Task<string?> FindQueuePredecessorAsync(TrafficRequestEntity? current, string taskId,
        string resourceId, int freeSlots, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        bool Fresh(TrafficRequestEntity row) => row.QueuePriority is { } priority && double.IsFinite(priority) &&
            row.QueueObservedAtUtc is { } observed && observed <= now && now - observed <= TimeSpan.FromSeconds(10);
        if (current is null || !Fresh(current)) return null;
        var cutoff = now.AddSeconds(-10);
        var requests = (await database.Queryable<TrafficRequestEntity>()
            .Where(r => r.State == "Waiting" && r.TaskId != taskId && r.QueuePriority != null &&
                r.QueueObservedAtUtc >= cutoff && r.QueueObservedAtUtc <= now)
            .ToListAsync(ct)).Where(r => Fresh(r) && (r.WaitingCategory is null or "Route") &&
                r.MapId == current.MapId && r.MapVersionId == current.MapVersionId &&
                Resources(r).Contains(resourceId, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (requests.Length == 0) return null;
        var taskIds = requests.Select(r => r.TaskId).Append(taskId).Distinct().ToArray();
        var stepIds = requests.Select(r => r.StepId).ToArray();
        var tasks = (await database.Queryable<DispatchTaskEntity>().Where(t => taskIds.Contains(t.TaskId))
            .ToListAsync(ct)).ToDictionary(t => t.TaskId, StringComparer.OrdinalIgnoreCase);
        if (!tasks.TryGetValue(taskId, out var currentTask)) return null;
        var steps = (await database.Queryable<DispatchTaskStepEntity>().Where(s => stepIds.Contains(s.StepId))
            .ToListAsync(ct)).ToDictionary(s => s.StepId, StringComparer.OrdinalIgnoreCase);
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().ToListAsync(ct);
        bool Ahead(TrafficRequestEntity row, DispatchTaskEntity task) =>
            row.QueuePriority > current.QueuePriority || row.QueuePriority == current.QueuePriority &&
                (task.CreatedAt < currentTask.CreatedAt || task.CreatedAt == currentTask.CreatedAt &&
                    StringComparer.Ordinal.Compare(task.TaskId, taskId) < 0);
        // Do not reserve a free resource for a predecessor that is still blocked
        // by a different full resource: acquisition remains all-or-nothing.
        bool CanAcquireAll(TrafficRequestEntity row) => Resources(row).All(id =>
            catalog.TryGet(id, out var definition) && definition.Capacity > 0 &&
            leases.Where(l => string.Equals(l.ResourceId, id, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(l.TaskId, row.TaskId, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < definition.Capacity);
        var ahead = requests.Where(r => tasks.TryGetValue(r.TaskId, out var task) &&
                Enum.IsDefined(typeof(DispatchTaskStage), task.Stage) && task.Stage is >= 0 and < 90 &&
                (string.IsNullOrWhiteSpace(task.VehicleId) || string.Equals(task.VehicleId, r.VehicleId, StringComparison.OrdinalIgnoreCase)) &&
                steps.TryGetValue(r.StepId, out var step) && step.TaskId == r.TaskId && step.Status is 0 or 10 or 60 &&
                !leases.Any(l => string.Equals(l.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(l.TaskId, r.TaskId, StringComparison.OrdinalIgnoreCase)) &&
                Ahead(r, task) && CanAcquireAll(r))
            .OrderByDescending(r => r.QueuePriority).ThenBy(r => tasks[r.TaskId].CreatedAt)
            .ThenBy(r => r.TaskId, StringComparer.Ordinal).DistinctBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase).ToArray();
        return ahead.Length >= freeSlots ? ahead[0].TaskId : null;
    }

    private static string[] Resources(TrafficRequestEntity request)
    {
        try { return JsonSerializer.Deserialize<string[]>(request.ResourceIds) ?? []; }
        catch (JsonException) { return []; }
    }
}
