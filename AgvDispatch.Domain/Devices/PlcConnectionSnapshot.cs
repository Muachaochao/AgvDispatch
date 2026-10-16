namespace AgvDispatch.Domain.Devices;

public sealed record PlcConnectionSnapshot(
    string DeviceId,
    string DisplayName,
    ConnectionState State,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastSuccessfulReadAt,
    DateTimeOffset? LastFailureAt,
    double? LastResponseTimeMs,
    string? Message);

public sealed record PlcConnectionChanged(
    ConnectionState PreviousState,
    ConnectionState CurrentState,
    DateTimeOffset OccurredAt,
    string? Message);
