using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>
/// Reads the commissioned maximum-load vehicle envelope used by task 28.
/// Missing or duplicate profiles deliberately yield no envelope.
/// </summary>
public sealed class ConfiguredTrafficBodyEnvelopeProvider(
    HandshakeExecutionOptions handshake,
    IActiveMapRuntime activeMap) : ITrafficBodyEnvelopeProvider
{
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct)
    {
        var map = activeMap.Current;
        var profiles = handshake.DumperExit.BodyProfiles
            .Where(profile => profile.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult<TrafficBodyEnvelope?>(
            map is null || profiles.Length != 1 ||
            !profiles[0].CalibrationVerified || !profiles[0].IncludesMaximumPermittedLoad
                ? null
                : new TrafficBodyEnvelope(profiles[0].ProfileId, vehicleId,
                    map.Definition.CoordinateSystem,
                    profiles[0].RadiusFromReportedReferenceMeters,
                    profiles[0].CalibrationVerified,
                    profiles[0].IncludesMaximumPermittedLoad,
                    profiles[0].LengthMeters,
                    profiles[0].WidthMeters));
    }
}
