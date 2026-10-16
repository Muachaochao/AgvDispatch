using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Domain.Agvs;

public sealed record AgvVehicleStatus(
    string VehicleId,
    ConnectionState Connection,
    string OperatingState,
    decimal BatteryPercent,
    string? CurrentStation,
    DateTimeOffset UpdatedAt);
