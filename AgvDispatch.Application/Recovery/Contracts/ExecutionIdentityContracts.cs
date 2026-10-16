using AgvDispatch.Domain.Devices;
namespace AgvDispatch.Application.Abstractions;

// IDs are opaque strings: never compare cycle counters across device restarts or infer IDs from Boolean bits.
public enum ContainerLoadState { Unknown = 0, Empty = 10, Loaded = 20 }
public sealed record PlcExecutionIdentity(string StationId, string TaskId, string DeviceSessionId, string CycleId,
    string ContainerId, string OriginalBusinessStepId, string? OriginalVendorTaskId,
    SignalQuality Quality, DateTimeOffset ObservedAt, bool IsCoherent, int SchemaVersion = 1);
public sealed record VehicleContainerIdentity(string VehicleId, ContainerLoadState LoadState, string? ContainerId,
    string? OriginalBusinessStepId, string? OriginalVendorTaskId, SignalQuality Quality,
    DateTimeOffset ObservedAt, bool IsCoherent, int SchemaVersion = 1);
public sealed record ExecutionIdentityBaseline(PlcExecutionIdentity? Plc, VehicleContainerIdentity? Vehicle, DateTimeOffset CapturedAt);
public sealed record RecoveryIdentityVerification(string State, string Summary, string? BlockReason,
    PlcExecutionIdentity? Plc = null, VehicleContainerIdentity? Vehicle = null, ExecutionIdentityBaseline? Baseline = null)
{
    public bool MachineVerified => State == "Verified";
}
public sealed class RecoveryIdentityOptions
{
    // Off-site development keeps this false; enabling it requires producer mappings and original intent baselines.
    public bool RequireMachineIdentity { get; set; }
}
public interface IExecutionIdentityCache
{
    bool TryGetPlc(string stationId, out PlcExecutionIdentity identity);
    bool TryGetVehicle(string vehicleId, out VehicleContainerIdentity identity);
    IReadOnlyList<PlcExecutionIdentity> GetPlc();
    IReadOnlyList<VehicleContainerIdentity> GetVehicles();
}
