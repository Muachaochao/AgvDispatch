namespace AgvDispatch.Domain.Devices;

public sealed record DeviceConnectionStatus(
    string DeviceId,
    string DisplayName,
    ConnectionState State,
    DateTimeOffset UpdatedAt,
    string? Message = null);
