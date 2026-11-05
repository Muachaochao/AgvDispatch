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
    DateTimeOffset UpdatedAt)
{
    public string TemplateNodeId { get; init; } = "";
    public string FailurePolicy { get; init; } = TaskTemplateExecutionPolicy.HoldUntilCondition;
    public string TimeoutAction { get; init; } = TaskTemplateExecutionPolicy.NotifyAndHold;
    public int TimeoutSeconds { get; init; }
    public bool AutoRetry { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}
