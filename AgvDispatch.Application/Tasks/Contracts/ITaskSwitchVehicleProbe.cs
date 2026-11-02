namespace AgvDispatch.Application.Abstractions;

// This is a fresh, read-only mechanical observation, not an authorization to move.
// Stop state, original transport identity, PLC ownership and traffic remain separate checks.
public sealed record TaskSwitchVehicleEvidence(string VehicleId, bool EmptyAndLowered,
    DateTimeOffset ObservedAt, IReadOnlyList<string> Blockers)
{
    public bool TasksKnown { get; init; }
    public bool NoActiveTasks { get; init; }
    public bool BusinessCleanupReady { get; init; }
    public IReadOnlyList<string> BusinessCleanupBlockers { get; init; } = [];
    public bool? Loaded { get; init; }
    public int? JackState { get; init; }
}

public interface ITaskSwitchVehicleProbe
{
    Task<TaskSwitchVehicleEvidence> ReadAsync(string vehicleId, CancellationToken ct);
}
