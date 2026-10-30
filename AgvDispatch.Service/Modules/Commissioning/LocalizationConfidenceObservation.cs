using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

public enum LocalizationObservationState { Ready, TransientLow, PersistentLow, Invalid }

// A grace period observes an already accepted navigation; it never supplies evidence
// for a new send, a PLC transition, arrival or resource release.
public sealed class LocalizationConfidenceObservation(TimeSpan holdAfter, TimeProvider? clock = null)
{
    private readonly object sync = new();
    private readonly Dictionary<string, (DateTimeOffset Last, DateTimeOffset? LowSince)> samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public static bool IsReady(AgvRobotSnapshot snapshot) =>
        snapshot.LocalizationConfidence is { } confidence && double.IsFinite(confidence) && confidence is >= .5 and <= 1;

    public void Clear(string vehicleId) { lock (sync) samples.Remove(vehicleId); }

    public LocalizationObservationState Observe(AgvRobotSnapshot? snapshot, TimeSpan maximumAge)
    {
        lock (sync)
        {
            if (snapshot is null) return LocalizationObservationState.Invalid;
            var id = snapshot.VehicleId;
            if (string.IsNullOrWhiteSpace(id)) return LocalizationObservationState.Invalid;
            var now = time.GetUtcNow();
            if (snapshot.Connection != ConnectionState.Online ||
                snapshot.Quality != SignalQuality.Good || snapshot.ConsecutiveFailures != 0 ||
                snapshot.LastSuccessfulPollAt is not { } at || at > now.AddSeconds(1) || now - at >= maximumAge ||
                snapshot.LocalizationConfidence is not { } confidence || !double.IsFinite(confidence) || confidence is < 0 or > 1)
            {
                samples.Remove(id);
                return LocalizationObservationState.Invalid;
            }
            samples.TryGetValue(id, out var previous);
            if (previous.Last != default && at < previous.Last)
            {
                samples.Remove(id);
                return LocalizationObservationState.Invalid;
            }
            if (confidence >= .5)
            {
                samples[id] = (at, null);
                return LocalizationObservationState.Ready;
            }
            var since = previous.LowSince;
            if (since is null || at - previous.Last >= maximumAge) since = at;
            samples[id] = (at, since);
            // Duplicate reads, browser refreshes and stopped polling cannot advance
            // the interval: only the vehicle's successful sample timestamp can.
            return at - since.Value >= holdAfter
                ? LocalizationObservationState.PersistentLow : LocalizationObservationState.TransientLow;
        }
    }
}
