namespace AgvDispatch.Application.Abstractions;

// The selected physical bay is immutable for this task, including recovery and cancellation.
public sealed record TaskWaitingBayBinding(string TaskId, string VehicleId, string StepId,
    string MapId, string MapVersionId, string WaitingSite, string ResourceId);

public interface ITaskWaitingBayAllocator
{
    Task<TaskWaitingBayBinding?> GetAsync(string taskId, CancellationToken ct);
    // Historical use is a preference, never proof that a physical bay is free.
    Task<IReadOnlyDictionary<string, int>> GetSelectionCountsAsync(string mapId, string versionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
    Task<TrafficReservationResult> TryAcquireAsync(TaskWaitingBayBinding binding,
        IReadOnlyCollection<string> routeResources, CancellationToken ct);
}
