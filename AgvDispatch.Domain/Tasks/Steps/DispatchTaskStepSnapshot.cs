namespace AgvDispatch.Domain.Tasks;

public sealed record DispatchTaskStepSnapshot(
    string StepId,
    string TaskId,
    int Sequence,
    DispatchTaskStepType StepType,
    DispatchTaskStepStatus Status,
    string? SourceSite,
    string? DestinationSite,
    string? VehicleId,
    string? VendorTaskId,
    int RetryCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);
