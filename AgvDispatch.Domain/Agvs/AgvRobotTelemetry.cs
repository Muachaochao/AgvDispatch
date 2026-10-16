namespace AgvDispatch.Domain.Agvs;

public sealed record AgvRobotTelemetry(
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
    DateTimeOffset ReadAt)
{
    public double? VelocityX { get; init; }
    public double? VelocityY { get; init; }
    public double? AngularVelocity { get; init; }
    public bool? Charging { get; init; }
    public double? BatteryVoltage { get; init; }
    public double? BatteryCurrent { get; init; }
    public double? BatteryTemperature { get; init; }
}
