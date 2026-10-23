namespace AgvDispatch.Application.Abstractions;

public sealed record NavigationTestQuery(string VehicleId, string SourceSite, string TargetSite);
public sealed record NavigationTestSubmit(string PreviewId, bool SiteClearConfirmed);
public sealed record NavigationTestCancel(string TaskId);
public sealed record NavigationTestPreview(string PreviewId, DateTimeOffset ExpiresAt, string VehicleId,
    string SourceSite, string TargetSite, string RobotMap, string RobotMapMd5, IReadOnlyList<string> Route);
public sealed record NavigationTestRecord(string TaskId, string VendorTaskId, string VehicleId,
    string SourceSite, string TargetSite, string RobotMap, string RobotMapMd5, string State,
    string Message, DateTimeOffset UpdatedAt, string Operator);
public sealed record NavigationTestStatus(bool Enabled, string VehicleId, NavigationTestRecord? Current,
    string Message)
{
    public bool Ready { get; init; }
    public string? BlockReason { get; init; }
}
public interface ISingleNavigationTestService
{
    Task<NavigationTestStatus> ReadAsync(CancellationToken ct);
    Task<NavigationTestPreview> PreviewAsync(TrafficUserContext user, NavigationTestQuery query, CancellationToken ct);
    Task<NavigationTestRecord> SubmitAsync(TrafficUserContext user, NavigationTestSubmit request, CancellationToken ct);
    Task<NavigationTestRecord> CancelAsync(TrafficUserContext user, NavigationTestCancel request, CancellationToken ct);
}
