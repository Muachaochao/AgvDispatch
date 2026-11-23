namespace AgvDispatch.Service;

public sealed class DispatchTaskStepOptions
{
    public const string SectionName = "DispatchTaskSteps";

    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 100;
    // Reserve and dispatch one bounded route window at a time. The vehicle stops
    // at a configured node role before requesting the next window.
    public bool RollingReservationEnabled { get; set; } = true;
    public int RollingWindowMinimumEdges { get; set; } = 2;
    public int RollingWindowMaximumEdges { get; set; } = 12;
    // R17: zero disables the hard ceiling for compatibility. When positive, a
    // route that cannot be split at authored safe stops is refused instead of
    // silently becoming one oversized atomic window.
    public int RollingWindowHardMaximumEdges { get; set; }
    // NearestSafeStop (default, unchanged behaviour): cut at the first safe stop after MinimumEdges;
    // MaximumEdges is not used. FarthestWithinMaximum: cut at the farthest safe stop within
    // [MinimumEdges, MaximumEdges] edges, so dense safe points do not force a stop-and-renew every
    // few metres; with no safe stop inside the range it falls back to the nearest one beyond it.
    public string RollingWindowBoundary { get; set; } = RollingWindowBoundaries.NearestSafeStop;
}

public static class RollingWindowBoundaries
{
    public const string NearestSafeStop = "NearestSafeStop";
    public const string FarthestWithinMaximum = "FarthestWithinMaximum";

    public static bool IsKnown(string? value) =>
        string.Equals(value, NearestSafeStop, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, FarthestWithinMaximum, StringComparison.OrdinalIgnoreCase);
}
