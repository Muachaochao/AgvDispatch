using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public sealed record ServiceRuntimeDiagnostic(
    string ServiceName,
    string Version,
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Uptime);

public sealed record DatabaseDiagnostic(
    bool Online,
    double ResponseTimeMs,
    string Message);

public sealed record PlcDiagnostic(
    string DeviceId,
    string DisplayName,
    ConnectionState State,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSuccessfulReadAt,
    double? LastResponseTimeMs,
    string? Message,
    long SignalVersion,
    int TotalSignals,
    int EnabledSignals,
    int GoodSignals,
    int StaleSignals,
    int BadSignals,
    int UnknownSignals,
    int DisabledSignals);

public sealed record AgvDiagnostic(
    string VehicleId,
    string DisplayName,
    string Endpoint,
    ConnectionState Connection,
    SignalQuality Quality,
    decimal? BatteryPercent,
    string? CurrentStation,
    string? NavigationState,
    int TaskCount,
    int AlarmCount,
    bool? Blocked,
    bool? EmergencyStopped,
    double? X,
    double? Y,
    double? Angle,
    double? LocalizationConfidence,
    AgvMapPositionState MapPositionState,
    double? DistanceToRouteMeters,
    string? NearestEdgeId,
    string MapPositionMessage,
    DateTimeOffset? LastSuccessfulPollAt,
    DateTimeOffset UpdatedAt,
    int ConsecutiveFailures,
    double? LastResponseTimeMs,
    string? ErrorMessage)
{
    public double? VelocityX { get; init; }
    public double? VelocityY { get; init; }
    public double? AngularVelocity { get; init; }
    public bool? Charging { get; init; }
    public double? BatteryVoltage { get; init; }
    public double? BatteryCurrent { get; init; }
    public double? BatteryTemperature { get; init; }
    public AgvObstacleDiagnostic? Obstacle { get; init; }
}

public sealed record AgvObstacleDiagnostic(
    string State,
    DateTimeOffset? Since,
    double? DurationSeconds,
    DateTimeOffset? LastClearedAt,
    string Source,
    string Message,
    string NavigationPolicy,
    string LocalDetourEvidence);

public enum AgvMapPositionState
{
    MapUnavailable = 0,
    PositionUnavailable = 10,
    Stale = 20,
    LowConfidence = 30,
    OffRoute = 40,
    Normal = 50
}

public sealed record AgvMapPositionAnalysis(
    AgvMapPositionState State,
    double? DistanceToRouteMeters,
    string? NearestEdgeId,
    string Message);

public sealed record DispatchTaskDiagnostic(
    string TaskId,
    string StationId,
    string? VehicleId,
    string? VendorTaskId,
    DispatchTaskStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaskDiagnostic(
    int ActiveCount,
    int WaitingCount,
    int RunningCount,
    int FaultedCount,
    IReadOnlyList<DispatchTaskDiagnostic> ActiveTasks);

public sealed record NamedPipeDiagnostic(
    string PipeName,
    int ActiveConnections,
    long TotalRequests,
    long FailedRequests,
    string CurrentWindowsIdentity,
    string CurrentDisplayName,
    TrafficPermission CurrentPermissions,
    string? LastClientIdentity,
    string? LastOperation,
    DateTimeOffset? LastRequestAt);

public sealed record SafetySwitchDiagnostic(
    bool PlcCommunicationEnabled,
    bool PlcWritesAllowed,
    bool AgvCommunicationEnabled,
    bool AgvCommandsAllowed,
    bool MapEnabled,
    bool DispatchingEnabled,
    string? ActiveMapId,
    string? ActiveMapVersionId);

public sealed record SystemStatusSnapshot(
    DateTimeOffset GeneratedAt,
    ServiceRuntimeDiagnostic Service,
    DatabaseDiagnostic Database,
    PlcDiagnostic Plc,
    IReadOnlyList<AgvDiagnostic> Agvs,
    TaskDiagnostic Tasks,
    NamedPipeDiagnostic NamedPipe,
    SafetySwitchDiagnostic Safety)
{
    public BackendExecutionDiagnostic? Execution { get; init; }
}

public interface ISystemStatusSnapshotService
{
    Task<SystemStatusSnapshot> GetAsync(
        TrafficUserContext user,
        CancellationToken cancellationToken);
}
