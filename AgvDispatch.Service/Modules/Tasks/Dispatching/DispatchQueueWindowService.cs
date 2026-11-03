using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class DispatchQueueWindowService(IDispatchOrchestrationRepository tasks,
    ITrafficResourceManager trafficResources, DispatchAssignmentOptimizer? optimizer = null) : IDispatchQueueWindowService
{
    private readonly DispatchAssignmentOptimizer _optimizer = optimizer ?? new();

    public async Task<DispatchQueueWindow> LoadAsync(DispatchAssignmentOptions policy,
        IReadOnlyList<DispatchTaskWorkItem> activeTasks, DateTimeOffset now, CancellationToken ct)
    {
        if (policy.GetConfigurationError() is { } error) throw new ArgumentException(error, nameof(policy));
        var waiting = (await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct)).ToArray();
        var heldTasks = await trafficResources.GetHeldTaskIdsAsync(ct);
        var occupied = activeTasks.Where(t => !string.IsNullOrWhiteSpace(t.VehicleId)).Select(t => t.VehicleId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        occupied.UnionWith(await trafficResources.GetHeldVehicleIdsAsync(ct));
        var fixedTaskIds = activeTasks.Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Repository stage filtering is repeated here so a malformed adapter cannot reintroduce
        // submitted/executing work into the matching graph. Leases stay fixed across prepare crashes.
        var work = waiting.Where(t => t.Stage == DispatchTaskStage.WaitingForVehicle &&
            string.IsNullOrWhiteSpace(t.VehicleId) && string.IsNullOrWhiteSpace(t.VendorTaskId) &&
            !heldTasks.Contains(t.TaskId) && !fixedTaskIds.Contains(t.TaskId)).ToArray();
        var input = work.Select(t => new DispatchAssignmentTask(t.TaskId, t.StationId, t.Priority, t.CreatedAt)
        {
            Deadline = policy.StationDeadlineMinutes.FirstOrDefault(p => p.Key.Equals(t.StationId, StringComparison.OrdinalIgnoreCase)).Value is > 0 and var minutes
                ? t.CreatedAt.AddMinutes(minutes) : null
        }).ToArray();
        var window = _optimizer.SelectWindow(input, policy, now);
        var windowIds = window.Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ready = (await tasks.GetReadyStepsAsync(int.MaxValue, ct))
            .Where(s => s.StepType == DispatchTaskStepType.GoToPickup && windowIds.Contains(s.TaskId))
            .GroupBy(s => s.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return new() { Waiting = waiting, Work = work, Input = input, Window = window, Ready = ready, OccupiedVehicles = occupied };
    }
}
