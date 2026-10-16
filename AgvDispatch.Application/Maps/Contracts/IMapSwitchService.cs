using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public enum MapSwitchPhase { Draining = 10, AwaitingResume = 20, Completed = 90, Cancelled = 100 }
public enum MapExecutionMode { Starting, Running, Draining, Held }
public enum MapExecutionOperation { NewWork, ExistingWork }

public sealed record MapSwitchSnapshot(string ReleaseId, string MapId, string VersionId,
    string? PreviousMapId, string? PreviousVersionId, MapSwitchPhase Phase,
    string OperatorIdentity, string Evidence, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt)
{
    public string PhaseText => Phase switch
    {
        MapSwitchPhase.Draining => "旧任务收尾", MapSwitchPhase.AwaitingResume => "已激活待放行",
        MapSwitchPhase.Completed => "已完成", MapSwitchPhase.Cancelled => "已取消", _ => "未知"
    };
}
public sealed record MapSwitchStatus(MapExecutionMode Mode, MapSwitchSnapshot? Current,
    IReadOnlyList<string> Blockers, IReadOnlyList<MapSwitchSnapshot> History);
public sealed record BeginMapSwitchRequest(string MapId, string VersionId);
public sealed record FinishMapSwitchRequest(string ReleaseId, bool SiteSafetyConfirmed,
    bool VehicleMapCompatibilityConfirmed, string Evidence, bool OriginalWorkflowConfirmed = false);
public sealed record MapSwitchResult(bool Accepted, string Message);

public sealed class MapSwitchSafetySettings
{
    // Include every commissioned vehicle, including offline vehicles. Empty means software-only preparation.
    public IReadOnlyList<string> RequiredVehicleIds { get; init; } = [];
    public bool CheckPlcSignals { get; init; }
    public TimeSpan StateMaxAge { get; init; } = TimeSpan.FromSeconds(5);
    public double MinimumLocalizationConfidence { get; init; } = 0.5;
    public double MaximumRouteDeviationMeters { get; init; } = 0.75;
    public double MaximumStationDeviationMeters { get; init; } = 0.3;
    public double StoppedVelocityTolerance { get; init; } = 0.01;
}

public interface IMapExecutionGate
{
    MapExecutionMode Mode { get; }
    bool CanStartNewWork { get; }
    ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation, CancellationToken cancellationToken);
    ValueTask<IDisposable> EnterControlAsync(CancellationToken cancellationToken);
    // Called only while holding the control/operation lease.
    void SetMode(MapExecutionMode mode);
}

public interface IMapSwitchService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<MapSwitchStatus> GetAsync(TrafficUserContext user, CancellationToken cancellationToken);
    Task<MapSwitchResult> BeginAsync(TrafficUserContext user, BeginMapSwitchRequest request, CancellationToken cancellationToken);
    Task<MapSwitchResult> ContinueDrainAsync(TrafficUserContext user, FinishMapSwitchRequest request, CancellationToken cancellationToken);
    Task<MapSwitchResult> ResumeAsync(TrafficUserContext user, FinishMapSwitchRequest request, CancellationToken cancellationToken);
    Task<MapSwitchResult> CancelAsync(TrafficUserContext user, FinishMapSwitchRequest request, CancellationToken cancellationToken);
    // Activation calls this while holding the common control lease.
    Task<IReadOnlyList<string>> GetBlockersAsync(MapDefinition definition, CancellationToken cancellationToken);
}
