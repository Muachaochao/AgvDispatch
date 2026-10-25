namespace AgvDispatch.Application.Abstractions;

public sealed class PlcCycleTestOptions
{
    public bool Enabled { get; init; }
    public string VehicleId { get; init; } = "";
    public string StationId { get; init; } = "CNC-001";
    public string DumperStationId { get; init; } = "DUMPER-001";
    public string PickupSite { get; init; } = "LM2";
    public string WaitingSite { get; init; } = "LM5";
    public string DumpingSite { get; init; } = "LM6";
    public int MaximumAgeSeconds { get; init; } = 5;
    public int StageTimeoutSeconds { get; init; } = 300;
}
public sealed record PlcCycleTestSignal(string StationId, string VariableName, bool? Value, string Quality, DateTimeOffset? ObservedAt);
public sealed record PlcCycleTestEnvironment(bool SimulatorOnly, bool BusinessExecutionDisabled, bool MapRunning,
    string MapId, string MapVersionId, string VehicleId, string? CurrentStation, bool VehicleOnline,
    bool VehicleStopped, bool VehicleEmpty, bool VehicleLocalized, bool VehicleHealthy, DateTimeOffset ObservedAt,
    IReadOnlyList<PlcCycleTestSignal> Signals)
{
    public string RobotMap { get; init; } = "";
    public string RobotMapMd5 { get; init; } = "";
}
public sealed record PlcCycleTestRoute(string SourceSite, string TargetSite, IReadOnlyList<string> Stations);
public sealed record PlcCycleTestRouteRequest(string VehicleId, string CurrentStation, string PickupSite, string WaitingSite, string DumpingSite);
public sealed record PlcCycleTestRouteProof(string RobotMap, string RobotMapMd5, IReadOnlyList<PlcCycleTestRoute> Routes, DateTimeOffset ObservedAt);
public interface IPlcCycleTestPort
{
    Task<PlcCycleTestEnvironment> ObserveAsync(CancellationToken ct);
    Task<PlcCycleTestRouteProof> CheckRoutesAsync(PlcCycleTestRouteRequest request, CancellationToken ct);
    Task WriteAsync(string stationId, string signalName, bool value, CancellationToken ct);
}
public sealed record PlcCycleTestArmRequest(string RequestId, string PickupSite, string WaitingSite, string DumpingSite,
    bool SiteClearConfirmed, bool EmptyVehicleConfirmed, bool SimulatorOnlyConfirmed);
public sealed record PlcCycleTestStopRequest(string CycleId, string Reason);
public sealed record PlcCycleTestResetRequest(string CycleId, string Reason);
public sealed record PlcCycleTestChild(string Kind, string RequestId, string? VendorTaskId, string Target, string State, DateTimeOffset UpdatedAt);
public sealed record PlcCycleTestEvent(DateTimeOffset OccurredAt, string Stage, string Message);
public sealed record PlcCycleTestPendingWrite(string StationId, string VariableName, bool Value, string NextStage, DateTimeOffset IntentAt, bool Acknowledged);
public sealed record PlcCycleTestRecord(string CycleId, string RequestId, string VehicleId, string StationId,
    string PickupSite, string WaitingSite, string DumpingSite, string MapId, string MapVersionId,
    string RobotMap, string RobotMapMd5, string Stage, string Message, string Operator,
    DateTimeOffset ArmedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PlcCycleTestChild> Children, IReadOnlyList<PlcCycleTestEvent> Events)
{
    public bool SimulationOnly { get; init; } = true;
    public bool PhysicalExitVerified { get; init; }
    public PlcCycleTestPendingWrite? PendingWrite { get; init; }
}
public sealed record PlcCycleTestStatus(bool Enabled, bool CanArm, string? BlockReason, string VehicleId,
    string StationId, string PickupSite, string WaitingSite, string DumpingSite, string Message, PlcCycleTestRecord? Current);
public interface IPlcCycleTestService
{
    Task<PlcCycleTestStatus> ReadAsync(CancellationToken ct);
    Task<PlcCycleTestStatus> ArmAsync(TrafficUserContext user, PlcCycleTestArmRequest request, CancellationToken ct);
    Task<PlcCycleTestStatus> StopAsync(TrafficUserContext user, PlcCycleTestStopRequest request, CancellationToken ct);
    Task<PlcCycleTestStatus> ResetAsync(TrafficUserContext user, PlcCycleTestResetRequest request, CancellationToken ct);
}
