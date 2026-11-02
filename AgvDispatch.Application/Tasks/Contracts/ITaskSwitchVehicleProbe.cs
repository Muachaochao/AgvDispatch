namespace AgvDispatch.Application.Abstractions;

// This is a fresh, read-only mechanical observation, not an authorization to move.
// Stop state, original transport identity, PLC ownership and traffic remain separate checks.
public sealed record TaskSwitchVehicleEvidence(string VehicleId, bool EmptyAndLowered,
    DateTimeOffset ObservedAt, IReadOnlyList<string> Blockers)
{
    public bool TasksKnown { get; init; }
    public bool NoActiveTasks { get; init; }
}

public interface ITaskSwitchVehicleProbe
{
    Task<TaskSwitchVehicleEvidence> ReadAsync(string vehicleId, CancellationToken ct);
}
