using AgvDispatch.Domain.Tasks;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public interface IAgvCommandOutboxRepository
{
    Task<DownstreamAdmissionSnapshot> GetPickupAdmissionAsync(string taskId, CancellationToken ct) =>
        Task.FromResult(new DownstreamAdmissionSnapshot(false, true, 0, [], "未启用下游准入限流。"));
    Task<bool> TryPrepareTransportStepAsync(
        string taskId,
        string stepId,
        string vehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        CancellationToken cancellationToken);

    Task<bool> TryPrepareTransportWindowAsync(
        string taskId,
        string stepId,
        string vehicleId,
        string fullSource,
        string fullDestination,
        AgvTransportRouteWindow window,
        IReadOnlyList<AgvTransportRouteWindow> windows,
        string? previousVendorTaskId,
        AgvTransportAction? action,
        CancellationToken cancellationToken) =>
        window.Index == 0
            ? TryPrepareTransportStepAsync(taskId, stepId, vehicleId, window.Path, action, cancellationToken)
            : Task.FromResult(false);

    Task<AgvCommandOutboxItem?> GetTransportCommandByVendorAsync(string taskId,
        string vendorTaskId, CancellationToken cancellationToken) => Task.FromResult<AgvCommandOutboxItem?>(null);

    Task<AgvCommandOutboxItem?> GetOpenTransportCommandAsync(string taskId,
        CancellationToken cancellationToken) => Task.FromResult<AgvCommandOutboxItem?>(null);

    Task<IReadOnlyList<AgvCommandOutboxItem>> GetDueCommandsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<AgvCommandOutboxItem?> TryClaimAsync(
        string commandId,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken);

    Task MarkSentAsync(
        string commandId,
        string vendorCommandId,
        CancellationToken cancellationToken);

    Task ScheduleRetryAsync(
        string commandId,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        int maximumAttempts,
        CancellationToken cancellationToken);

    Task DeferBeforeSendAsync(
        string commandId,
        string errorMessage,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    Task<int> RecoverStaleClaimsAsync(
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken);
}
