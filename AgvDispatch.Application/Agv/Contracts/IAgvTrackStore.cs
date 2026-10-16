namespace AgvDispatch.Application.Abstractions;

public sealed record AgvTrackPointSnapshot(
    long Id,
    string VehicleId,
    string MapId,
    string MapVersionId,
    double X,
    double Y,
    double? Angle,
    double? LocalizationConfidence,
    AgvMapPositionState PositionState,
    double? DistanceToRouteMeters,
    string? NearestEdgeId,
    DateTimeOffset SampledAt);

public sealed record AgvTrackQueryRequest(
    string VehicleId,
    string MapId,
    DateTimeOffset From,
    DateTimeOffset To,
    int MaximumCount = 5000,
    string? MapVersionId = null);

public interface IAgvTrackStore
{
    Task AppendAsync(AgvTrackPointSnapshot point, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgvTrackPointSnapshot>> QueryAsync(
        AgvTrackQueryRequest request,
        CancellationToken cancellationToken);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
