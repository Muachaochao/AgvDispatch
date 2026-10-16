using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed partial class TrafficResourceControlService
{
    public Task<TrafficMapSnapshot> GetMapAsync(TrafficMapQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var map = activeMap?.Current;
        var unchanged = map != null && map.MapId == query.CachedMapId && map.VersionId == query.CachedVersionId;
        return Task.FromResult(new TrafficMapSnapshot(DateTimeOffset.Now, map?.MapId, map?.VersionId,
            unchanged ? null : map?.Definition, (vehicleStates?.GetAll() ?? []).Select(v => new TrafficMapVehicle(v.VehicleId,
                v.X, v.Y, v.LocalizationConfidence, v.Connection, v.Quality, v.LastSuccessfulPollAt)).ToArray(),
            mapOptions?.PositionMaxAgeSeconds ?? 5, mapOptions?.MinimumLocalizationConfidence ?? 0.5,
            mapOptions?.MaximumRouteDeviationMeters ?? 0.75));
    }
}
