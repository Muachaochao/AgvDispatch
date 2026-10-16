using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Domain.Agvs;

public sealed record SeerTaskSnapshot(
    string TaskId,
    int Status,
    int? Type);

public sealed record AgvRobotSnapshot(
    string VehicleId,
    string DisplayName,
    string IpAddress,
    int Port,
    ConnectionState Connection,
    SignalQuality Quality,
    double? X,
    double? Y,
    double? Angle,
    double? LocalizationConfidence,
    string? CurrentStation,
    string? LastStation,
    decimal? BatteryPercent,
    bool? Blocked,
    bool? EmergencyStopped,
    string? NavigationState,
    string? SourceStation,
    string? TargetStation,
    double? PathPercentage,
    IReadOnlyList<SeerTaskSnapshot> Tasks,
    IReadOnlyList<AgvAlarmSnapshot> Alarms,
    DateTimeOffset? LastSuccessfulPollAt,
    DateTimeOffset UpdatedAt,
    int ConsecutiveFailures,
    double? LastResponseTimeMs,
    string? ErrorMessage,
    long Version)
{
    public double? VelocityX { get; init; }
    public double? VelocityY { get; init; }
    public double? AngularVelocity { get; init; }
    public bool? Charging { get; init; }
    public double? BatteryVoltage { get; init; }
    public double? BatteryCurrent { get; init; }
    public double? BatteryTemperature { get; init; }
}

public sealed record AgvRobotStateChanged(
    string VehicleId,
    AgvRobotSnapshot? Previous,
    AgvRobotSnapshot Current,
    DateTimeOffset OccurredAt);
