namespace AgvDispatch.Application.Abstractions;

public enum ChargingWorkflowKind { Dock, Charge, Undock }
public enum ChargingWorkflowState { Running, Succeeded, Failed, Unknown }

public sealed record ChargingChargerReading(string ChargerIp, bool? IsAuto, bool? Charging,
    double? Current, double? Voltage, string[] Alarms, DateTimeOffset ObservedAt);

public sealed record ChargingWorkflowStatus(string OperationId, ChargingWorkflowKind Kind,
    ChargingWorkflowState State, DateTimeOffset UpdatedAt, string Message)
{
    public ChargingBatteryReading? Battery { get; init; }
    public ChargingChargerReading? Station { get; init; }
    public double ContinuousPositiveSeconds { get; init; }
    public bool ShutdownVerified { get; init; }
    public string? EvidenceDirectory { get; init; }
}

/// <summary>The caller durably saves a unique operation intent before StartAsync.
/// Observations never start/replay an operation. Unknown results retain ownership.</summary>
public interface IChargingWorkflow
{
    bool IsConfigured(ChargingSite site, string vehicleId);
    Task StartAsync(ChargingSession session, string operationId, ChargingWorkflowKind kind, CancellationToken ct);
    Task<ChargingWorkflowStatus> ObserveAsync(ChargingSession session, CancellationToken ct);
    Task RequestStopAsync(ChargingSession session, CancellationToken ct);
}

public sealed class ChargingScriptOptions
{
    public bool Enabled { get; set; }
    public string PythonExecutable { get; set; } = "python";
    public string ToolsDirectory { get; set; } = "charging-tools";
    public string EvidenceDirectory { get; set; } = "charging-workflows";
    public int LeaseTimeoutSeconds { get; set; } = 15;
    public ChargingScriptProfile[] Profiles { get; set; } = [];
}

public sealed class ChargingScriptProfile
{
    public string SiteId { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public string ProfilePath { get; set; } = "";
}
