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
        bool Fresh(TrafficRequestEntity row) => (row.QueuePriority is null || double.IsFinite(row.QueuePriority.Value)) &&
            row.QueueObservedAtUtc is { } observed && observed <= now && now - observed <= TimeSpan.FromSeconds(10);
        if (current is null || !Fresh(current)) return null;
        var cutoff = now.AddSeconds(-10);
        var requests = (await database.Queryable<TrafficRequestEntity>()
            .Where(r => r.State == "Waiting" && r.TaskId != taskId &&
                r.QueueObservedAtUtc >= cutoff && r.QueueObservedAtUtc <= now)
            .ToListAsync(ct)).Where(r => Fresh(r) && (r.WaitingCategory is null or "Route" or "Equipment") &&
                (current.MapId is null || r.MapId == current.MapId && r.MapVersionId == current.MapVersionId) &&
                Resources(r).Contains(resourceId, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (requests.Length == 0) return null;
        var taskIds = requests.Select(r => r.TaskId).Append(taskId).Distinct().ToArray();
        var stepIds = requests.Select(r => r.StepId).Append(current.StepId).Distinct().ToArray();
        var tasks = (await database.Queryable<DispatchTaskEntity>().Where(t => taskIds.Contains(t.TaskId))
            .ToListAsync(ct)).ToDictionary(t => t.TaskId, StringComparer.OrdinalIgnoreCase);
        var steps = (await database.Queryable<DispatchTaskStepEntity>().Where(s => stepIds.Contains(s.StepId))
            .ToListAsync(ct)).ToDictionary(s => s.StepId, StringComparer.OrdinalIgnoreCase);
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().ToListAsync(ct);
        // System movements (charging and returning to standby) have no DispatchTask.
        // They participate at ordinary priority zero, FIFO by their durable request,
        // instead of bypassing a valid business queue with a null priority.
        double Priority(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task)
            ? row.QueuePriority ?? task.Priority : 0;
        DateTime CalledAt(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task)
            ? task.CreatedAt : row.RequestedAt;
        bool ValidOwner(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task)
            ? Enum.IsDefined(typeof(DispatchTaskStage), task.Stage) && task.Stage is >= 0 and < 90 &&
                (string.IsNullOrWhiteSpace(task.VehicleId) || string.Equals(task.VehicleId, row.VehicleId, StringComparison.OrdinalIgnoreCase)) &&
                steps.TryGetValue(row.StepId, out var step) && step.TaskId == row.TaskId && step.Status is 0 or 10 or 40 or 60
            // A missing business task with an explicit optimizer score must not
            // become a new system owner. Only ordinary system requests lack it.
            : row.QueuePriority is null;
        // No priority bonus: carrying a full box is a tie-break only. The original
        // effective priority/age policy and existing physical ownership remain authoritative.
        bool FullBoxContinuation(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task) &&
            steps.TryGetValue(row.StepId, out var step) && step.TaskId == row.TaskId &&
            (step.StepType == (int)DispatchTaskStepType.DeliverFullBoxToDumper && task.Stage is 30 or 40 ||
             step.StepType == (int)DispatchTaskStepType.EnterDumpingPosition && task.Stage == 50);
        bool Ahead(TrafficRequestEntity row) =>
            Priority(row) > Priority(current) || Priority(row) == Priority(current) &&
                (FullBoxContinuation(row) && !FullBoxContinuation(current) || FullBoxContinuation(row) == FullBoxContinuation(current) &&
                (CalledAt(row) < CalledAt(current) || CalledAt(row) == CalledAt(current) &&
                    StringComparer.Ordinal.Compare(row.TaskId, taskId) < 0));
        // Do not reserve a free resource for a predecessor that is still blocked
        // by a different full resource: acquisition remains all-or-nothing.
        bool CanAcquireAll(TrafficRequestEntity row) => Resources(row).All(id =>
            catalog.TryGet(id, out var definition) && definition.Capacity > 0 &&
            leases.Where(l => string.Equals(l.ResourceId, id, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(l.TaskId, row.TaskId, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < definition.Capacity);
        var ahead = requests.Where(r => ValidOwner(r) &&
                !leases.Any(l => string.Equals(l.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(l.TaskId, r.TaskId, StringComparison.OrdinalIgnoreCase)) &&
                Ahead(r) && CanAcquireAll(r))
            .OrderByDescending(Priority).ThenByDescending(FullBoxContinuation).ThenBy(CalledAt)
            .ThenBy(r => r.TaskId, StringComparer.Ordinal).DistinctBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase).ToArray();
        return ahead.Length >= freeSlots ? ahead[0].TaskId : null;
    }

    private static string[] Resources(TrafficRequestEntity request)
    {
        try { return JsonSerializer.Deserialize<string[]>(request.ResourceIds) ?? []; }
        catch (JsonException) { return []; }
    }
}
