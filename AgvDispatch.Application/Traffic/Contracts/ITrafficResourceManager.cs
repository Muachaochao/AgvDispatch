namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficReservationResult(
    bool Acquired,
    string? BlockingResourceId = null,
    string? BlockingTaskId = null);

public interface ITrafficResourceManager
{
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
