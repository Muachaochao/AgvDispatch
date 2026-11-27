namespace AgvDispatch.Service;

/// <summary>
/// Vehicle firmware can publish a short blocked signal while yielding to another
/// vehicle. A hold is warranted only when the vehicle is stationary and blocked
/// continuously for the configured interval; movement or a clear sample resets it.
/// </summary>
public sealed class BlockedObservationTracker(TimeSpan holdAfter, TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> since = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider time = clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider;

    public bool Observe(string vehicleId, bool blocked, bool moving, DateTimeOffset? observedAt = null)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return false;
        lock (sync)
        {
            var now = observedAt ?? time.GetUtcNow();
            if (!blocked || moving)
            {
                since.Remove(vehicleId);
                return false;
            }
            if (!since.TryGetValue(vehicleId, out var started) || now < started)
            {
                since[vehicleId] = now;
                started = now;
            }
            return now - started >= holdAfter;
        }
    }

    public void Clear(string vehicleId)
    {
        lock (sync) since.Remove(vehicleId);
    }
}
