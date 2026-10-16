using AgvDispatch.Domain.Tasks;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public interface IAgvCommandOutboxRepository
{
    Task<bool> TryPrepareTransportStepAsync(
        string taskId,
        string stepId,
        string vehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        CancellationToken cancellationToken);

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

    Task<int> RecoverStaleClaimsAsync(
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken);
}
