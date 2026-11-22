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
public sealed record NavigationTestPause(string TaskId);
public sealed record NavigationTestCancel(string TaskId);
public sealed record NavigationTestResume(string TaskId);
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
    public DateTimeOffset? BlockedSince { get; init; }
    public IReadOnlyList<string> VendorTaskIds { get; init; } = [];
    public NavigationTestDetourRecord? Detour { get; init; }
    public NavigationTestAutoDetourRecord? AutoDetour { get; init; }
    public DateTimeOffset? CancellationAttemptedAt { get; init; }
    public string? CancellationVendorTaskId { get; init; }
    public DateTimeOffset? PauseAttemptedAt { get; init; }
    public string? PauseVendorTaskId { get; init; }
    public int PauseStoppedSamples { get; init; }
    public DateTimeOffset? PauseLastObservedAt { get; init; }
    public DateTimeOffset? PauseStoppedAt { get; init; }
    public DateTimeOffset? ResumeAttemptedAt { get; init; }
    public string? ResumeVendorTaskId { get; init; }
}
public sealed record NavigationTestStatus(bool Enabled, string VehicleId, NavigationTestRecord? Current,
    string Message)
{
    public bool Ready { get; init; }
    public string? BlockReason { get; init; }
    public bool AutoDetourEnabled { get; init; }
    public int AutoDetourWaitSeconds { get; init; }
}
public sealed record JackTestSubmit(string RequestId,string Action,bool AreaClearConfirmed,bool EmptyLoadConfirmed);
public sealed record JackTestTelemetry(int State,bool Enabled,bool Emergency,int ErrorCode,bool HasLoad,double? Height,DateTimeOffset ObservedAt);
public sealed record JackTestRecord(string RequestId,string VehicleId,string Action,string State,string Message,int BeforeState,DateTimeOffset UpdatedAt,string Operator)
{public bool Acknowledged {get;init;} }
public sealed record JackTestStatus(bool Ready,string? BlockReason,string VehicleId,JackTestTelemetry? Telemetry,JackTestRecord? Current);
public sealed record ForkTestSubmit(string RequestId,string Action,bool AreaClearConfirmed,bool EmptyLoadConfirmed);
public sealed record ForkTestTelemetry(bool UpperLimit,bool LowerLimit,bool PumpOn,bool ValveOn,DateTimeOffset ObservedAt);
public sealed record ForkTestRecord(string RequestId,string VehicleId,string Action,string State,string Message,DateTimeOffset UpdatedAt)
{
    public bool Acknowledged {get;init;}
    public IReadOnlyList<string> RequestIds {get;init;} = [];
}
public sealed record ForkTestStatus(bool Supported,bool Ready,string? BlockReason,string VehicleId,ForkTestTelemetry? Telemetry,ForkTestRecord? Current);
public interface ISingleNavigationTestService
{
    Task<NavigationTestDetourPreview> PreviewDetourAsync(TrafficUserContext user, NavigationTestDetourQuery request, CancellationToken ct) => throw new NotSupportedException("受控改道后台未更新。");
    Task<NavigationTestRecord> ConfirmDetourAsync(TrafficUserContext user, NavigationTestDetourConfirm request, CancellationToken ct) => throw new NotSupportedException("受控改道后台未更新。");
    Task<NavigationTestRecord> ContinueDetourAsync(TrafficUserContext user, NavigationTestDetourContinue request, CancellationToken ct) => throw new NotSupportedException("受控改道后台未更新。");
    /// <summary>Advances only the explicitly enabled single-vehicle automatic
    /// detour state machine. It performs no work unless AutoDetourEnabled.</summary>
    Task AdvanceAutoDetourAsync(CancellationToken ct) => Task.CompletedTask;
    Task<ForkTestStatus> ReadForkAsync(CancellationToken ct) => throw new NotSupportedException("货叉测试后台未更新。");
    Task<ForkTestRecord> SubmitForkAsync(TrafficUserContext user,ForkTestSubmit request,CancellationToken ct) => throw new NotSupportedException("货叉测试后台未更新。");
    Task<JackTestStatus> ReadJackAsync(CancellationToken ct) => throw new NotSupportedException("顶升测试后台未更新。");
    Task<JackTestRecord> SubmitJackAsync(TrafficUserContext user,JackTestSubmit request,CancellationToken ct) => throw new NotSupportedException("顶升测试后台未更新。");
    Task<NavigationTestStatus> ReadAsync(CancellationToken ct);
    Task<NavigationTestStations> ReadStationsAsync(CancellationToken ct);
    Task<NavigationTestPreview> PreviewAsync(TrafficUserContext user, NavigationTestQuery query, CancellationToken ct);
    Task<NavigationTestRecord> SubmitAsync(TrafficUserContext user, NavigationTestSubmit request, CancellationToken ct);
    Task<NavigationTestRecord> PauseAsync(TrafficUserContext user, NavigationTestPause request, CancellationToken ct) =>
        throw new NotSupportedException("后台尚未支持暂停当前导航。");
    Task<NavigationTestRecord> CancelAsync(TrafficUserContext user, NavigationTestCancel request, CancellationToken ct);
    Task<NavigationTestRecord> ResumeAsync(TrafficUserContext user, NavigationTestResume request, CancellationToken ct) => throw new NotSupportedException("后台尚未支持继续暂停的原导航。");
    Task ValidateRetainedBusinessRunAsync(string retainedVehicleId, CancellationToken ct) =>
        throw new NotSupportedException("独立叉车测试后台未更新。");
}
