using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

/// <summary>The same original call and policy feed assignment and every later resource request.</summary>
public static class DispatchTaskPriority
{
    public static DispatchAssignmentTask Input(DispatchTaskWorkItem task, DispatchAssignmentOptions policy) =>
        new(task.TaskId, task.StationId, task.Priority, task.CreatedAt)
        {
            Deadline = policy.StationDeadlineMinutes.FirstOrDefault(p =>
                p.Key.Equals(task.StationId, StringComparison.OrdinalIgnoreCase)).Value is > 0 and var minutes
                ? task.CreatedAt.AddMinutes(minutes) : null
        };

    public static DispatchAssignmentTaskScore Score(DispatchTaskWorkItem task, DispatchAssignmentOptions policy,
        DateTimeOffset now) => new DispatchAssignmentOptimizer().Score(Input(task, policy), policy, now);
}
