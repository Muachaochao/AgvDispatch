namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficRouteObservation(string StepId, string TaskId, string VehicleId, string Source,
    string Destination, IReadOnlyList<string> EdgeIds, IReadOnlyList<string> ResourceIds, bool Waiting, string Reason)
{
    public string WaitingCategory { get; init; } = "Route";
    public string? MapId { get; init; }
    public string? MapVersionId { get; init; }
    /// <summary>Rolling optimizer's effective priority; null preserves legacy acquisition behavior.</summary>
    public double? QueuePriority { get; init; }
}
public sealed record TrafficHeldResource(string ResourceId, string? MapId, string? MapVersionId);
public sealed record TrafficBlocker(string ResourceId, string TaskId, string VehicleId, string StepId, bool HeartbeatExpired)
{
    public string? MapId { get; init; }
    public string? MapVersionId { get; init; }
}
public sealed record TrafficRequestSnapshot(string StepId, string TaskId, string VehicleId, string Source, string Destination,
    IReadOnlyList<string> EdgeIds, IReadOnlyList<string> ResourceIds, string State, string Reason,
    IReadOnlyList<TrafficBlocker> Blockers, DateTimeOffset RequestedAt, DateTimeOffset UpdatedAt)
{
    public string WaitingCategory { get; init; } = "Route";
    public string? MapId { get; init; }
    public string? MapVersionId { get; init; }
    public int? Priority { get; init; }
    public DateTimeOffset? WaitingSince { get; init; }
    public bool WaitingStartKnown { get; init; }
    public double? WaitingSeconds { get; init; }
    public bool WaitingClockValid { get; init; } = true;
    public TrafficWaitingAlertLevel WaitingAlertLevel { get; init; }
    public int WaitingWarningSeconds { get; init; } = 60;
    public int WaitingCriticalSeconds { get; init; } = 300;
    public DateTimeOffset? BlockingSince { get; init; }
    public bool BlockingStartKnown { get; init; }
    public bool BlockingObservationCurrent { get; init; }
    public bool BlockingClockValid { get; init; } = true;
    public DateTimeOffset? LastBlockingClearedAt { get; init; }
    public IReadOnlyList<TrafficHeldResource> HeldResources { get; init; } = [];
    public IReadOnlyList<string> HeldResourceIds { get; init; } = [];
}
public sealed record TrafficLifecycleEvent(string EventId, string StepId, string TaskId, string VehicleId,
    string Kind, string Reason, IReadOnlyList<string> ResourceIds, DateTimeOffset OccurredAt)
{
    public DateTimeOffset? WaitingSince { get; init; }
    public bool WaitingStartKnown { get; init; }
}
public sealed record TrafficLifecycleSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<TrafficRequestSnapshot> Requests,
    IReadOnlyList<TrafficLifecycleEvent> Events, bool RequestsTruncated = false);

public enum TrafficWaitingSort { LongestWait = 0, PriorityThenWait = 10 }
public sealed record TrafficWaitingQuery(string? BlockingResourceId = null, int? MinimumPriority = null,
    TrafficWaitingSort Sort = TrafficWaitingSort.LongestWait, int PageNumber = 1, int PageSize = 50);
public sealed record TrafficWaitingPage(DateTimeOffset CapturedAt, int TotalWaiting, int TotalMatched,
    int PageNumber, int PageSize, IReadOnlyList<TrafficRequestSnapshot> Items, IReadOnlyList<string> BlockingResources)
{
    // Computed from the complete read snapshot, independently of filtering and pagination.
    public IReadOnlyList<TrafficCycleRisk> CycleRisks { get; init; } = [];
    public string CycleAnalysisMessage { get; init; } = "仅观察持久化任务等待和资源占用，不证明物理死锁，也不授予释放许可。";
}
public sealed record TrafficCycleRiskLink(string WaitingTaskId, string WaitingStepId, string WaitingVehicleId,
    string HoldingTaskId, string HoldingStepId, string HoldingVehicleId, string ResourceId,
    bool HeartbeatExpired, string? MapId, string? MapVersionId);
public sealed record TrafficCycleRisk(string CycleId, IReadOnlyList<string> TaskIds,
    IReadOnlyList<string> VehicleIds, IReadOnlyList<string> ResourceIds,
    IReadOnlyList<TrafficCycleRiskLink> Links, string Message);
