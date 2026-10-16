namespace AgvDispatch.Application.Abstractions;

public interface ITrafficBodyEnvelopeProvider
{
    Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct);
}

// Only a persisted step identifier is accepted; callers cannot supply ownership or route claims.
public interface ITrafficSegmentReleaseInspector
{
    Task<TrafficSegmentReleaseAssessment> InspectAsync(string stepId, CancellationToken ct);
}
