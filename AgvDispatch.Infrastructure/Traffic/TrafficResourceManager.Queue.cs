using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceManager
{
    private async Task<ILookup<string, string>> LoadQueuePredecessorsAsync(TrafficRequestEntity? current,
        string taskId, string[] requested, ILookup<string, TrafficResourceLeaseEntity> leases, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var comparer = StringComparer.OrdinalIgnoreCase;
        var requestedIds = requested.ToHashSet(comparer);
        static ILookup<string, string> Empty() => Array.Empty<string>().ToLookup(id => id);
        var cutoff = now.AddSeconds(-10);
        bool Fresh(TrafficRequestEntity row) => (row.QueuePriority is null || double.IsFinite(row.QueuePriority.Value)) &&
            row.QueueObservedAtUtc is { } observed && observed >= cutoff && observed <= now;
        if (current is null || !Fresh(current)) return Empty();
        var requests = (await database.Queryable<TrafficRequestEntity>()
            .Where(r => r.State == "Waiting" && r.TaskId != taskId &&
                r.QueueObservedAtUtc >= cutoff && r.QueueObservedAtUtc <= now)
            .ToListAsync(ct)).Where(r => Fresh(r) && (r.WaitingCategory is null or "Route" or "Equipment") &&
                (current.MapId is null || r.MapId == current.MapId && r.MapVersionId == current.MapVersionId))
            .Select(row => (Row: row, Resources: Resources(row).Distinct(comparer).ToArray()))
            .Where(request => request.Resources.Any(requestedIds.Contains)).ToArray();
        if (requests.Length == 0) return Empty();
        var taskIds = requests.Select(r => r.Row.TaskId).Append(taskId).Distinct().ToArray();
        var stepIds = requests.Select(r => r.Row.StepId).Append(current.StepId).Distinct().ToArray();
        var tasks = (await database.Queryable<DispatchTaskEntity>().Where(t => taskIds.Contains(t.TaskId))
            .ToListAsync(ct)).ToDictionary(t => t.TaskId, StringComparer.OrdinalIgnoreCase);
        var steps = (await database.Queryable<DispatchTaskStepEntity>().Where(s => stepIds.Contains(s.StepId))
            .ToListAsync(ct)).ToDictionary(s => s.StepId, StringComparer.OrdinalIgnoreCase);
        // System movements (charging and returning to standby) have no DispatchTask.
        // They participate at ordinary priority zero, FIFO by their durable request,
        // instead of bypassing a valid business queue with a null priority.
        // B07-FIX C5: a charging bay reservation uses its explicit charging priority; other system movements stay at zero.
        double Priority(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task)
            ? row.QueuePriority ?? task.Priority : IsChargingBayRequest(row) ? row.QueuePriority ?? 0 : 0;
        DateTime CalledAt(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task)
            ? task.CreatedAt : row.RequestedAt;
        bool ValidOwner(TrafficRequestEntity row) => tasks.TryGetValue(row.TaskId, out var task)
            ? Enum.IsDefined(typeof(DispatchTaskStage), task.Stage) && task.Stage is >= 0 and < 90 &&
                (string.IsNullOrWhiteSpace(task.VehicleId) || string.Equals(task.VehicleId, row.VehicleId, StringComparison.OrdinalIgnoreCase)) &&
                steps.TryGetValue(row.StepId, out var step) && step.TaskId == row.TaskId && step.Status is 0 or 10 or 40 or 60
            // A missing business task with an explicit optimizer score must not
            // become a new system owner. Only ordinary system requests lack it.
            : row.QueuePriority is null || IsChargingBayRequest(row);
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
        bool CanAcquireAll(string owner, string[] resourceIds) => resourceIds.All(id =>
            catalog.TryGet(id, out var definition) && definition.Capacity > 0 &&
            leases[id].Where(l => !comparer.Equals(l.TaskId, owner))
                .Select(l => l.TaskId).Distinct(comparer).Count() < definition.Capacity);
        return requests.Where(r => ValidOwner(r.Row) && Ahead(r.Row) && CanAcquireAll(r.Row.TaskId, r.Resources))
            .OrderByDescending(r => Priority(r.Row)).ThenByDescending(r => FullBoxContinuation(r.Row))
            .ThenBy(r => CalledAt(r.Row)).ThenBy(r => r.Row.TaskId, StringComparer.Ordinal)
            .SelectMany(r => r.Resources.Where(id => requestedIds.Contains(id) &&
                !leases[id].Any(l => comparer.Equals(l.TaskId, r.Row.TaskId)))
                .Select(id => (ResourceId: id, r.Row.TaskId)))
            .ToLookup(item => item.ResourceId, item => item.TaskId, comparer);
    }

    // B07-FIX C5: a charging bay reservation carries an explicit charging priority and is a system owner, not an optimizer score.
    private static bool IsChargingBayRequest(TrafficRequestEntity row) =>
        row.TaskId.StartsWith("CHARGE-", StringComparison.Ordinal) && row.StepId.EndsWith("-LOCK", StringComparison.Ordinal);

    private static string[] Resources(TrafficRequestEntity request)
    {
        try { return JsonSerializer.Deserialize<string[]>(request.ResourceIds) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<TrafficCycleRisk?> CycleIfGrantedAsync(TrafficRequestEntity context,
        string ownerStepId, string taskId, string vehicleId, string[] requested,
        IReadOnlyList<TrafficResourceLeaseEntity> leases, DateTime now, CancellationToken ct)
    {
        var waiting = (await database.Queryable<TrafficRequestEntity>()
                .Where(row => row.State == "Waiting")
                .ToListAsync(ct))
            .Where(row => !string.Equals(row.StepId, ownerStepId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var taskIds = waiting.Select(row => row.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var stepIds = waiting.Select(row => row.StepId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tasks = (await database.Queryable<DispatchTaskEntity>()
                .Where(row => taskIds.Contains(row.TaskId)).ToListAsync(ct))
            .ToDictionary(row => row.TaskId, StringComparer.OrdinalIgnoreCase);
        var steps = (await database.Queryable<DispatchTaskStepEntity>()
                .Where(row => stepIds.Contains(row.StepId)).ToListAsync(ct))
            .ToDictionary(row => row.StepId, StringComparer.OrdinalIgnoreCase);
        var hypothetical = leases.ToList();
        foreach (var resourceId in requested.Where(resourceId =>
                     !leases.Any(lease => string.Equals(lease.TaskId, taskId, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(lease.ResourceId, resourceId, StringComparison.OrdinalIgnoreCase))))
        {
            hypothetical.Add(new TrafficResourceLeaseEntity
            {
                LeaseId = "HYPOTHETICAL-" + Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                OwnerStepId = ownerStepId,
                TaskId = taskId,
                VehicleId = vehicleId,
                MapId = context.MapId,
                MapVersionId = context.MapVersionId,
                AcquiredAt = now,
                RenewedAt = now,
                ExpiresAt = now.Add(settings.LeaseDuration)
            });
        }
        return TrafficCycleRiskDetector.Find(waiting, tasks, steps, hypothetical, catalog, now)
            .FirstOrDefault(cycle => cycle.TaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase));
    }
}
