using AgvDispatch.Domain.Devices;
namespace AgvDispatch.Application.Abstractions;

// Future protocol implementations expose reads only. SnapshotVersion must change on EVERY identity change.
// A PLC writer must mark an update in progress before mutating fields; a stable even seqlock or equivalent
// atomic revision protocol is required. Merely reading the same cycle counter twice is insufficient.
public sealed record PlcIdentityStamp(string DeviceSessionId, string CycleId, string SnapshotVersion,
    bool IsStable, SignalQuality Quality);
public sealed record PlcIdentityFields(string StationId, string TaskId, string ContainerId,
    string OriginalBusinessStepId, string? OriginalVendorTaskId, SignalQuality Quality, int SchemaVersion = 1);
public interface IPlcExecutionIdentityReader
{
    string StationId { get; }
    Task<PlcIdentityStamp> ReadStampAsync(CancellationToken ct);
    Task<PlcIdentityFields> ReadFieldsAsync(CancellationToken ct);
}

// IsAtomic is a transport/protocol guarantee for the full payload, never inferred from successful JSON parsing.
public sealed record VehicleIdentityFrame(string VehicleId, ContainerLoadState LoadState, string? ContainerId,
    string? OriginalBusinessStepId, string? OriginalVendorTaskId, bool IsAtomic, SignalQuality Quality, int SchemaVersion = 1);
public interface IVehicleContainerIdentityReader
{
    string VehicleId { get; }
    Task<VehicleIdentityFrame> ReadAtomicAsync(CancellationToken ct);
}
public sealed class ExecutionIdentityAcquisitionOptions
{
    public bool Enabled { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    public int ReadTimeoutMs { get; set; } = 2000;
    public int CoherentReadAttempts { get; set; } = 3;
}
public sealed record IdentityAcquisitionResult(string SourceKind, string SourceId, string State, string Message);
