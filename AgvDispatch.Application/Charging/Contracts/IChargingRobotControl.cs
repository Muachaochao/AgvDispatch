namespace AgvDispatch.Application.Abstractions;

/// <summary>Robot-side charging relay and fresh battery observations. Relay acknowledgement
/// does not prove docking, charger handshake, or current flow. The caller persists the send
/// intent before writing and must not automatically repeat a write after an uncertain result.</summary>
public interface IChargingRobotControl
{
    Task SetChargingRelayAsync(string vehicleId, bool enabled, CancellationToken cancellationToken);
    Task<ChargingBatteryReading> ReadBatteryAsync(string vehicleId, CancellationToken cancellationToken);
}

public sealed record ChargingBatteryReading(
    string VehicleId,
    decimal? BatteryPercent,
    bool? Charging,
    double? Current,
    double? Temperature,
    bool? ManualCharge,
    bool? AutoCharge,
    DateTimeOffset ObservedAt);
