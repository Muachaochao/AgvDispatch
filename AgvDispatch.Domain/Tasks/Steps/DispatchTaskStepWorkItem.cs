namespace AgvDispatch.Domain.Tasks;

public sealed record DispatchTaskStepWorkItem(
    string TaskId,
    string StationId,
    string? VehicleId,
    DispatchTaskStage TaskStage,
    string StepId,
    int Sequence,
    DispatchTaskStepType StepType,
    DispatchTaskStepStatus StepStatus,
    string? SourceSite,
    string? DestinationSite,
    DateTimeOffset UpdatedAt);
