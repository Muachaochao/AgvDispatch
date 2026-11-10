using System.Collections.Concurrent;

namespace AgvDispatch.Application.Tasks;

/// <summary>Bounded first-pickup deferral. This never grants a route or moves a vehicle.</summary>
public sealed class TemporaryDetourDeferral
{
    private readonly ConcurrentDictionary<(string Task, string Vehicle, string Map), DateTimeOffset> firstSeen = new();

    public double RemainingSeconds(string task, string vehicle, string map, double travelSeconds,
        double referenceSeconds, DateTimeOffset now, double maximumRatio = 4,
        double minimumExtraSeconds = 60, double maximumDeferralSeconds = 120)
    {
        var key = (task, vehicle, map);
        if (!double.IsFinite(travelSeconds) || !double.IsFinite(referenceSeconds) || referenceSeconds <= 0 ||
            travelSeconds <= referenceSeconds * maximumRatio || travelSeconds - referenceSeconds < minimumExtraSeconds ||
            maximumDeferralSeconds <= 0)
        {
            firstSeen.TryRemove(key, out _);
            return 0;
        }
        var start = firstSeen.GetOrAdd(key, now);
        return Math.Max(0, maximumDeferralSeconds - Math.Max(0, (now - start).TotalSeconds));
    }

    public void RetainTasks(IReadOnlySet<string> waitingTasks)
    {
        foreach (var key in firstSeen.Keys)
            if (!waitingTasks.Contains(key.Task)) firstSeen.TryRemove(key, out _);
    }
}
