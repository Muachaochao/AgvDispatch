using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class DispatchQueueWindowService(IDispatchOrchestrationRepository tasks,
    ITrafficResourceManager trafficResources, DispatchAssignmentOptimizer? optimizer = null,
    DispatchTaskStepOptions? stepOptions = null) : IDispatchQueueWindowService
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
        var candidates = waiting.Where(t => t.Stage == DispatchTaskStage.WaitingForVehicle &&
            string.IsNullOrWhiteSpace(t.VehicleId) && string.IsNullOrWhiteSpace(t.VendorTaskId) &&
            !heldTasks.Contains(t.TaskId) && !fixedTaskIds.Contains(t.TaskId)).ToArray();
        var readySteps = (await tasks.GetReadyStepsAsync(int.MaxValue, ct))
            .Where(s => s.StepType == DispatchTaskStepType.GoToPickup)
            .GroupBy(s => s.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Selection rules apply to every dispatch mode, not only the legacy loop: a task with unfinished
        // predecessors never enters the window, and an expired SelectAgv stop policy stops it here.
        var dependencyIds = candidates.SelectMany(t => t.DependsOnTaskIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dependencyStates = dependencyIds.Length == 0
            ? new Dictionary<string, DispatchTaskDependencyState>(StringComparer.OrdinalIgnoreCase)
            : (await tasks.GetTaskDependencyStatesAsync(dependencyIds, ct))
                .ToDictionary(state => state.TaskId, StringComparer.OrdinalIgnoreCase);
        var work = new List<DispatchTaskWorkItem>(candidates.Length);
        foreach (var task in candidates)
        {
            if (!DependenciesComplete(task, dependencyStates)) continue;
            if (readySteps.TryGetValue(task.TaskId, out var step) &&
                await StopIfSelectionTimedOutAsync(task, step, SelectionStartedAt(task, dependencyStates), now, ct)) continue;
            work.Add(task);
        }
        var input = work.Select(t => DispatchTaskPriority.Input(t, policy)).ToArray();
        var window = _optimizer.SelectWindow(input, policy, now);
        var windowIds = window.Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ready = readySteps.Where(pair => windowIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new() { Waiting = waiting, Work = work.ToArray(), Input = input, Window = window, Ready = ready, OccupiedVehicles = occupied };
    }

    internal static bool DependenciesComplete(DispatchTaskWorkItem task,
        IReadOnlyDictionary<string, DispatchTaskDependencyState> states) =>
        task.DependsOnTaskIds.All(id => states.TryGetValue(id, out var state) &&
            state.Exists && state.Stage == DispatchTaskStage.Completed);

    private static DateTimeOffset SelectionStartedAt(DispatchTaskWorkItem task,
        IReadOnlyDictionary<string, DispatchTaskDependencyState> states)
    {
        var started = task.PolicyStartedAt;
        var dependencyReadyAt = task.DependsOnTaskIds
            .Select(id => states.TryGetValue(id, out var state) ? state.UpdatedAt : null)
            .Where(value => value is not null).Max();
        return dependencyReadyAt is { } ready && ready > started ? ready : started;
    }

    private async Task<bool> StopIfSelectionTimedOutAsync(DispatchTaskWorkItem task, DispatchTaskStepWorkItem step,
        DateTimeOffset startedAt, DateTimeOffset now, CancellationToken ct)
    {
        var policy = TaskWatchdogPolicyResolver.ForSelection(task, stepOptions ?? new());
        if (!TaskTemplateExecutionPolicy.HasTimedOut(startedAt, policy.TimeoutSeconds, now)) return false;
        var action = policy.Action;
        if (!TaskTemplateExecutionPolicy.StopsAutomationForTimeout(action)) return false;
        var reason = $"SelectAgv 已等待 {(long)(now - startedAt).TotalSeconds} 秒，超过阈值 {policy.TimeoutSeconds} 秒（来源 {policy.Source}）；" +
                     $"超时处置 {action}，保留原任务与现场责任，禁止自动重发。";
        return await tasks.StopReadyStepForPolicyAsync(task.TaskId, step.StepId, action, reason, ct);
    }
}
