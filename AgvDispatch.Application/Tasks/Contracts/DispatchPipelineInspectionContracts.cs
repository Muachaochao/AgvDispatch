namespace AgvDispatch.Application.Abstractions;

/// <summary>A read-only observation. Checks do not run or grant the dispatch safety gate.</summary>
public sealed record DispatchPipelineInspectionSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }
    public long CycleNumber { get; init; }
    public DateTimeOffset? OptimizationCapturedAt { get; init; }
    public string Mode { get; init; } = "Unknown";
    public string Summary { get; init; } = "";
    public IReadOnlyList<DispatchPipelineGate> Gates { get; init; } = [];
    public IReadOnlyList<DispatchPipelineInspectionLayer> Layers { get; init; } = [];
}

public sealed record DispatchPipelineGate(string Key, string Label, bool? Enabled, string Detail);
public sealed record DispatchPipelineMetric(string Label, string Value, string? Unit = null);
// Check states: Pass / Fail / Waiting / Disabled / Unknown / NotChecked / Stale.
public sealed record DispatchPipelineCheck(string Key, string Label, string State, string Actual,
    string Expected, string Reason, DateTimeOffset? ObservedAt = null);
public sealed record DispatchPipelineFact(string Label, string Value);
public sealed record DispatchPipelineInspectionRecord
{
    public string Id { get; init; } = "";
    public string? TaskId { get; init; }
    public string? StationId { get; init; }
    public string? VehicleId { get; init; }
    public string? ResourceId { get; init; }
    public string State { get; init; } = "Unknown";
    public string Title { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? Link { get; init; }
    public IReadOnlyList<DispatchPipelineFact> Facts { get; init; } = [];
}
// Layer state has the same meaning as DispatchPipelineLayerSnapshot, plus Unknown / Stale.
public sealed record DispatchPipelineInspectionLayer
{
    public string Key { get; init; } = "";
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string State { get; init; } = "Unknown";
    public string Summary { get; init; } = "";
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<DispatchPipelineMetric> Metrics { get; init; } = [];
    public IReadOnlyList<DispatchPipelineCheck> Checks { get; init; } = [];
    public IReadOnlyList<DispatchPipelineInspectionRecord> Records { get; init; } = [];
    public int TotalCount { get; init; }
    public bool Truncated { get; init; }
    public string Note { get; init; } = "";
}

public interface IDispatchPipelineInspectionService
{
    Task<DispatchPipelineInspectionSnapshot> GetAsync(CancellationToken cancellationToken);
}
