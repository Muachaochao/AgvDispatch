using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Domain.Stations;

public sealed record StationStateSnapshot(
    string StationId,
    StationOperationalState State,
    bool CanCreateTask,
    string? BlockReason,
    bool? FullBoxRequested,
    bool? BoxPresent,
    bool? Faulted,
    bool? CommunicationOnline,
    SignalQuality SignalQuality,
    string? ActiveTaskId,
    int ActiveTaskCount,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset StateChangedAt,
    long Version);

public sealed record StationStateChanged(
    string StationId,
    StationStateSnapshot? Previous,
    StationStateSnapshot Current,
    DateTimeOffset OccurredAt);
