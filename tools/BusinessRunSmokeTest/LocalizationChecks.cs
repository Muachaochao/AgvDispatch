using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Service;

internal static class LocalizationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var observation = new LocalizationConfidenceObservation(TimeSpan.FromSeconds(3), clock);
        var maximumAge = TimeSpan.FromSeconds(3);
        AgvRobotSnapshot Sample(double? confidence, string id = "A") => new(id, id, "synthetic", 0,
            ConnectionState.Online, SignalQuality.Good, 0, 0, 0, confidence, "PARK", null, 90,
            false, false, "Idle", null, null, null, [], [], clock.GetUtcNow(), clock.GetUtcNow(), 0, 0, null, 1);
        LocalizationObservationState Observe(AgvRobotSnapshot sample) => observation.Observe(sample, maximumAge);
        check(Observe(Sample(.99)) == LocalizationObservationState.Ready, "valid localization is ready");
        var first = Sample(.487);
        check(Observe(first) == LocalizationObservationState.TransientLow, "one low sample does not freeze an accepted navigation");
        clock.Advance(2);
        check(Observe(first) == LocalizationObservationState.TransientLow, "repeated reads do not accumulate low localization duration");
        check(Observe(Sample(.9, "B")) == LocalizationObservationState.Ready, "one vehicle's dip does not change another vehicle's health");
        check(Observe(Sample(.48)) == LocalizationObservationState.TransientLow, "two seconds of fresh low samples remain in observation");
        clock.Advance(1);
        check(Observe(Sample(.48)) == LocalizationObservationState.PersistentLow, "three seconds of fresh low samples require recovery");
        check(Observe(Sample(.5)) == LocalizationObservationState.Ready, "a valid threshold recovery clears the previous interval");
        check(Observe(Sample(.48)) == LocalizationObservationState.TransientLow, "a new dip starts a new observation interval");
        clock.Advance(3);
        check(Observe(Sample(.48)) == LocalizationObservationState.TransientLow, "a stale sampling gap cannot prove continuous low localization");
        clock.Advance(1);
        check(Observe(Sample(.48) with { Quality = SignalQuality.Bad }) == LocalizationObservationState.Invalid, "failed quality is never debounced");
        check(Observe(Sample(.48)) == LocalizationObservationState.TransientLow, "quality recovery starts a new interval");
        foreach (var confidence in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -.1, 1.1 })
        {
            check(Observe(Sample(confidence)) == LocalizationObservationState.Invalid, "missing or nonfinite/out-of-range confidence is invalid: " + confidence);
            check(!LocalizationConfidenceObservation.IsReady(Sample(confidence)), "invalid confidence is never arrival or send evidence: " + confidence);
        }
        foreach (var confidence in new[] { 0d, .487, .4999 })
            check(!LocalizationConfidenceObservation.IsReady(Sample(confidence)), "transient low values never authorize a new send or arrival: " + confidence);
        check(Observe(Sample(.8) with { Connection = ConnectionState.Interrupted }) == LocalizationObservationState.Invalid, "communication loss retains safety priority");
        check(Observe(Sample(.8) with { ConsecutiveFailures = 1 }) == LocalizationObservationState.Invalid, "failed polling cannot borrow a prior good sample");
        check(Observe(Sample(.8) with { LastSuccessfulPollAt = clock.GetUtcNow().AddSeconds(2) }) == LocalizationObservationState.Invalid, "future samples cannot supply evidence");
        check(Observe(Sample(.8) with { LastSuccessfulPollAt = clock.GetUtcNow().AddSeconds(-3) }) == LocalizationObservationState.Invalid, "expired samples are immediately invalid");
        Observe(Sample(.48)); clock.Advance(1); Observe(Sample(.48));
        check(Observe(Sample(.48) with { LastSuccessfulPollAt = clock.GetUtcNow().AddSeconds(-.5) }) == LocalizationObservationState.Invalid, "out-of-order telemetry breaks continuous timing");
        check(Observe(Sample(.48)) == LocalizationObservationState.TransientLow, "valid observations after reordering start again");
        observation.Clear("A");
        clock.Advance(1);
        check(Observe(Sample(.48)) == LocalizationObservationState.TransientLow, "missing vehicle state can explicitly reset its timer");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(double seconds) => now = now.AddSeconds(seconds);
    }
}
