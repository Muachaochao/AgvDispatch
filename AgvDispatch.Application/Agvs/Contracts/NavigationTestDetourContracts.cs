namespace AgvDispatch.Application.Abstractions;

/// <summary>A planning boundary only: the adapter must bind the robot map and coordinates to the
/// currently closed topology. A station name shared by two maps is never sufficient evidence.</summary>
public interface INavigationTestDetourPlanner
{
    Task<NavigationTestDetourPlan> PreviewAsync(NavigationTestDetourPlanRequest request, CancellationToken ct);
    Task ValidateAsync(NavigationTestDetourPlan plan, CancellationToken ct);
    /// <summary>Validate the exact plan/revision and hold closure mutations until the returned lease
    /// is disposed. Throw when stale; this call must not move a robot or mutate closures.</summary>
    ValueTask<IDisposable> EnterSendAsync(NavigationTestDetourPlan plan, CancellationToken ct);
}

public sealed record NavigationTestDetourPlanRequest(string TaskId, string VehicleId,
    string SourceSite, string TargetSite, string RobotMap, string RobotMapMd5,
    IReadOnlyList<NavigationTestStation> Stations, IReadOnlyList<string> OriginalRoute)
{
    /// <summary>Current route leg inferred from the actual robot station. The planner
    /// still validates this pair against the exact active and downloaded maps.</summary>
    public IReadOnlyList<NavigationTestSitePair> SuggestedBlockedPairs { get; init; } = [];
}

public sealed record NavigationTestSitePair(string FromSite, string ToSite);

public sealed record NavigationTestDetourPlan(bool Available, string Message, long ClosureRevision,
    string MapId, string VersionId, string DefinitionHash, string RobotMap, string RobotMapMd5,
    string SourceSite, string TargetSite, IReadOnlyList<string> Route)
{
    public IReadOnlyList<string> BlockedEdgeIds { get; init; } = [];
}

public sealed record NavigationTestDetourQuery(string TaskId);
public sealed record NavigationTestDetourConfirm(string TaskId, string PreviewId, bool CancelOriginalConfirmed);
public sealed record NavigationTestDetourContinue(string TaskId, string PreviewId, bool ExecuteDetourConfirmed);
public sealed record NavigationTestDetourPreview(string PreviewId, DateTimeOffset ExpiresAt,
    string TaskId, string VehicleId, DateTimeOffset? BlockedSince, NavigationTestDetourPlan Plan);

public sealed record NavigationTestDetourRecord(string PreviewId, string State, string Message,
    NavigationTestDetourPlan Plan, IReadOnlyList<string> OriginalVendorTaskIds,
    IReadOnlyList<string> OriginalRoute, string OriginalSourceSite, int OriginalNavigationApi,
    DateTimeOffset ConfirmedAt, string Operator)
{
    public string? CancelTargetId { get; init; }
    public DateTimeOffset? CancelAttemptedAt { get; init; }
    public DateTimeOffset? StoppedObservedAt { get; init; }
    public DateTimeOffset? SendAttemptedAt { get; init; }
    public IReadOnlyList<string> NewVendorTaskIds { get; init; } = [];
}
