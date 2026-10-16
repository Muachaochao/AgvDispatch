namespace AgvDispatch.Application.Abstractions;

public enum ManualReleaseWorkflowStatus
{
    PendingApproval = 0,
    Executed = 10,
    Rejected = 20,
    Expired = 30
}

public sealed record SubmitManualReleaseRequest(
    string LeaseId,
    string Reason,
    bool VehicleClearConfirmed,
    string ConfirmationText);

public sealed record ReviewManualReleaseRequest(
    string RequestId,
    bool Approve,
    string Comment);

public sealed record ManualReleaseWorkflowItem(
    string RequestId,
    string LeaseId,
    string ResourceId,
    string TaskId,
    string VehicleId,
    string RequestedBy,
    string RequestedByDisplayName,
    string Reason,
    ManualReleaseWorkflowStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewComment);

public sealed record ManualReleaseWorkflowResult(
    bool Accepted,
    string Message,
    string? RequestId = null);

public interface IManualReleaseWorkflowService
{
    Task<IReadOnlyList<ManualReleaseWorkflowItem>> GetRequestsAsync(
        TrafficUserContext user,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<ManualReleaseWorkflowResult> SubmitAsync(
        TrafficUserContext user,
        SubmitManualReleaseRequest request,
        CancellationToken cancellationToken);

    Task<ManualReleaseWorkflowResult> ReviewAsync(
        TrafficUserContext user,
        ReviewManualReleaseRequest request,
        CancellationToken cancellationToken);
}
