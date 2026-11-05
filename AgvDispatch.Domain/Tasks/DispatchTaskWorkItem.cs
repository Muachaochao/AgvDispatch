namespace AgvDispatch.Domain.Tasks;

public sealed record DispatchTaskWorkItem(
    string TaskId,
    string StationId,
    string? VehicleId,
    string? VendorTaskId,
    DispatchTaskStage Stage,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? RequestedVehicleId { get; init; }
    public string TemplateNodeId { get; init; } = "";
    public string FailurePolicy { get; init; } = TaskTemplateExecutionPolicy.HoldUntilCondition;
    public string TimeoutAction { get; init; } = TaskTemplateExecutionPolicy.NotifyAndHold;
    public int TimeoutSeconds { get; init; }
    public bool AutoRetry { get; init; }
    public DateTimeOffset PolicyStartedAt { get; init; }
    public IReadOnlyList<string> DependsOnTaskIds { get; init; } = [];
}
