namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficReservationResult(
    bool Acquired,
    string? BlockingResourceId = null,
    string? BlockingTaskId = null);

public interface ITrafficResourceManager
{
    // Explicit layer-four lifecycle work; candidate route readers must stay read-only.
    Task PrepareDispatchCycleAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

    Task<IReadOnlyList<string>?> GetStepHeldResourceIdsAsync(string taskId, string ownerStepId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    Task<TrafficReservationResult> TryAcquireAsync(
        string ownerStepId,
        string taskId,
        string vehicleId,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken);

    Task ReleaseStepAsync(string ownerStepId, CancellationToken cancellationToken);

    /// <summary>
    /// Startup sweep: rolls back leases of Ready steps that have no transport command, left
    /// behind when a crash interrupted acquisition before registration. Returns the step count.
    /// </summary>
    Task<int> RollbackAbandonedReadyStepLeasesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    Task<int> ReleaseStepExceptResourcesAsync(string ownerStepId,
        IReadOnlyCollection<string> keepResourceIds, string reason, CancellationToken cancellationToken) =>
        Task.FromResult(0);

    Task ReleaseTaskExceptStepAsync(
        string taskId,
        string ownerStepId,
        CancellationToken cancellationToken);

    Task ReleaseTaskAsync(string taskId, CancellationToken cancellationToken);

    Task RenewTaskAsync(string taskId, CancellationToken cancellationToken);
}
