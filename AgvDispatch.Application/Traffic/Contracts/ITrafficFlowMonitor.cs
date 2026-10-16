namespace AgvDispatch.Application.Abstractions;

public enum EdgeTrafficState
{
    Free = 0,
    Occupied = 10,
    Congested = 20,
    Stalled = 30
}

public sealed record EdgeTrafficSnapshot(
    string EdgeId,
    string From,
    string To,
    EdgeTrafficState State,
    IReadOnlyList<string> VehicleIds,
    IReadOnlyList<string> ResourceIds,
    DateTimeOffset StateSince,
    double DynamicPenalty,
    string Message);

public sealed record TrafficDeadlockCandidate(
    string CandidateId,
    string EdgeId,
    IReadOnlyList<string> VehicleIds,
    IReadOnlyList<string> ResourceIds,
    DateTimeOffset DetectedAt,
    string Reason,
    string Suggestion);

public sealed record TrafficFlowSnapshot(
    DateTimeOffset GeneratedAt,
    string? MapId,
    string? MapVersionId,
    IReadOnlyList<EdgeTrafficSnapshot> Edges,
    IReadOnlyList<TrafficDeadlockCandidate> Deadlocks);

public interface ITrafficFlowMonitor
{
    TrafficFlowSnapshot GetSnapshot();
    IReadOnlyDictionary<string, double> GetEdgePenalties();
}
