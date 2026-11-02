namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficReservationResult(
    bool Acquired,
    string? BlockingResourceId = null,
    string? BlockingTaskId = null);

public interface ITrafficResourceManager
{
    // A lease remains fixed even when a previous prepare crashed before assigning
    // a vehicle to its task. Optimizers must never reclaim such work implicitly.
    Task<IReadOnlySet<string>> GetHeldTaskIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    Task<IReadOnlySet<string>> GetHeldVehicleIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    /// <summary>Null means the current persisted route and each required lease match the imminent command.</summary>
    Task<string?> CheckStepReservationAsync(string stepId, string taskId, string vehicleId,
        string? mapId, string? mapVersionId, string source, string destination, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
    Task RecordRouteAsync(TrafficRouteObservation route, CancellationToken ct) => Task.CompletedTask;
    Task<IReadOnlySet<string>> GetUnavailableResourceIdsAsync(
        string requestingTaskId,
        CancellationToken cancellationToken);

    Task<TrafficReservationResult> TryAcquireAsync(
        string ownerStepId,
        string taskId,
        string vehicleId,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken);

    Task ReleaseStepAsync(string ownerStepId, CancellationToken cancellationToken);

    Task ReleaseTaskExceptStepAsync(
        string taskId,
        string ownerStepId,
        CancellationToken cancellationToken);

    Task ReleaseTaskAsync(string taskId, CancellationToken cancellationToken);

    Task RenewTaskAsync(string taskId, CancellationToken cancellationToken);
}
