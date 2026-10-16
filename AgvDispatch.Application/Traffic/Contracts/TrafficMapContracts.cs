using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Devices;
namespace AgvDispatch.Application.Abstractions;
public sealed record TrafficMapQuery(string? CachedMapId = null, string? CachedVersionId = null);
public sealed record TrafficMapVehicle(string VehicleId, double? X, double? Y, double? LocalizationConfidence,
    ConnectionState Connection, SignalQuality Quality, DateTimeOffset? LastSuccessfulPollAt);
public sealed record TrafficMapSnapshot(DateTimeOffset CapturedAt, string? MapId, string? VersionId,
    MapDefinition? Definition, IReadOnlyList<TrafficMapVehicle> Vehicles, double PositionMaxAgeSeconds = 5,
    double MinimumLocalizationConfidence = 0.5, double MaximumRouteDeviationMeters = 0.75);

public sealed class TrafficMapDisplayOptions
{
    public double PositionMaxAgeSeconds { get; set; } = 5;
    public double MinimumLocalizationConfidence { get; set; } = 0.5;
    public double MaximumRouteDeviationMeters { get; set; } = 0.75;
}
