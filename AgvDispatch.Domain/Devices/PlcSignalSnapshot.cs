namespace AgvDispatch.Domain.Devices;

public sealed record PlcSignalSnapshot(
    string StationId,
    string VariableName,
    string ChineseName,
    string Address,
    string DataType,
    string Direction,
    string Access,
    bool Enabled,
    bool? Value,
    SignalQuality Quality,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ChangedAt,
    long Version,
    string? ErrorMessage = null);

public sealed record PlcSignalReading(
    string StationId,
    string VariableName,
    bool Value,
    DateTimeOffset ReadAt);

public sealed record PlcSignalChanged(
    string StationId,
    string VariableName,
    bool? PreviousValue,
    bool? CurrentValue,
    SignalQuality PreviousQuality,
    SignalQuality CurrentQuality,
    DateTimeOffset OccurredAt,
    long Version);
