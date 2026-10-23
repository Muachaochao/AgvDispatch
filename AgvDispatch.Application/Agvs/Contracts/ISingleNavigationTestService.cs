namespace AgvDispatch.Application.Abstractions;

public sealed record NavigationTestQuery(string VehicleId, string SourceSite, string TargetSite)
{
    public string? ExpectedRobotMap { get; init; }
    public string? ExpectedRobotMapMd5 { get; init; }
}
public sealed record NavigationTestStation(string Id, double X, double Y);
public sealed record NavigationTestStations(string VehicleId, string RobotMap, string RobotMapMd5,
    IReadOnlyList<NavigationTestStation> Stations, string? SuggestedSourceSite, DateTimeOffset CapturedAt);
public sealed record NavigationTestSubmit(string PreviewId, bool SiteClearConfirmed);
public sealed record NavigationTestCancel(string TaskId);
public sealed record NavigationTestPreview(string PreviewId, DateTimeOffset ExpiresAt, string VehicleId,
    string SourceSite, string TargetSite, string RobotMap, string RobotMapMd5, IReadOnlyList<string> Route)
{
    public double? TargetX { get; init; }
    public double? TargetY { get; init; }
}
public sealed record NavigationTestRecord(string TaskId, string VendorTaskId, string VehicleId,
    string SourceSite, string TargetSite, string RobotMap, string RobotMapMd5, string State,
    string Message, DateTimeOffset UpdatedAt, string Operator)
{
    // Absent in historical records: these used the original 3066 single-edge implementation.
    public int NavigationApi { get; init; } = 3066;
    public IReadOnlyList<string> Route { get; init; } = [];
    public double? TargetX { get; init; }
    public double? TargetY { get; init; }
    public string? CurrentStation { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}
public sealed record NavigationTestStatus(bool Enabled, string VehicleId, NavigationTestRecord? Current,
    string Message)
{
    public bool Ready { get; init; }
    public string? BlockReason { get; init; }
}
public interface ISingleNavigationTestService
{
    Task<NavigationTestStatus> ReadAsync(CancellationToken ct);
    Task<NavigationTestStations> ReadStationsAsync(CancellationToken ct);
    Task<NavigationTestPreview> PreviewAsync(TrafficUserContext user, NavigationTestQuery query, CancellationToken ct);
    Task<NavigationTestRecord> SubmitAsync(TrafficUserContext user, NavigationTestSubmit request, CancellationToken ct);
    Task<NavigationTestRecord> CancelAsync(TrafficUserContext user, NavigationTestCancel request, CancellationToken ct);
}
