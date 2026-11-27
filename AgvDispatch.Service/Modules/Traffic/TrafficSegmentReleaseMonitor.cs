using System.Collections.Concurrent;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed record TrafficSegmentReleaseObservation(
    string TaskId,
    string StepId,
    string VehicleId,
    string State,
    string Reason,
    DateTimeOffset ObservedAt,
    IReadOnlyList<TrafficSegmentReleaseCandidate> Candidates,
    TrafficSegmentProgressSummary? Progress);

/// <summary>
/// In-memory read-only view of the latest automatic pass-through assessment.
/// It never grants or releases traffic rights.
/// </summary>
public sealed class TrafficSegmentReleaseMonitor(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, TrafficSegmentReleaseObservation> _latest =
        new(StringComparer.Ordinal);

    public void Update(TrafficSegmentReleaseObservation observation)
    {
        var now = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
        foreach (var item in _latest)
            if (now - item.Value.ObservedAt > TimeSpan.FromHours(1))
                _latest.TryRemove(item.Key, out _);
        _latest[observation.StepId] = observation;
    }

    public IReadOnlyList<TrafficSegmentReleaseObservation> Get() =>
        _latest.Values.OrderByDescending(value => value.ObservedAt).ToArray();

    public IReadOnlyList<TrafficSegmentReleaseObservation> Active(TimeSpan maximumAge)
    {
        var now = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
        return Get().Where(value => now - value.ObservedAt <= maximumAge).ToArray();
    }
}
