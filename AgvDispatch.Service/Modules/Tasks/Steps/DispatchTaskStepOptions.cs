using AgvDispatch.Domain.Tasks;

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
    // B07-R8 batch route rights: when the next rolling window is partly held by vehicles that
    // are only passing through (their remaining route never needs the stretch in front of us),
    // advance to the farthest free safe stop instead of waiting for the whole window. Any
    // unknown holder (charging, manual, parked, unreadable command) keeps the atomic window.
    public bool PartialWindowAcquisition { get; set; } = true;
    public int PartialWindowMinimumEdges { get; set; } = 1;

    // D6 fail-safe watchdogs are used only when the task has no template node
    // and no persisted task-parameter timeout. An explicit template or saved
    // parameter value, including zero, always wins.
    public int SelectAgvTimeoutSeconds { get; set; } = 300;
    public string DefaultTimeoutAction { get; set; } = TaskTemplateExecutionPolicy.ManualRecovery;
    public Dictionary<string, int> DefaultStepTimeoutSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(DispatchTaskStepType.GoToPickup)] = 900,
        [nameof(DispatchTaskStepType.PickupFullBox)] = 300,
        [nameof(DispatchTaskStepType.DeliverFullBoxToDumper)] = 900,
        [nameof(DispatchTaskStepType.WaitForDumperReady)] = 1800,
        [nameof(DispatchTaskStepType.EnterDumpingPosition)] = 600,
        [nameof(DispatchTaskStepType.DumpFullBox)] = 1800,
        [nameof(DispatchTaskStepType.ReturnEmptyBox)] = 900,
        [nameof(DispatchTaskStepType.PlaceEmptyBox)] = 300,
        [nameof(DispatchTaskStepType.CompletePlcHandshake)] = 300
    };

    public int GetDefaultStepTimeoutSeconds(DispatchTaskStepType stepType) =>
        DefaultStepTimeoutSeconds.TryGetValue(stepType.ToString(), out var seconds)
            ? Math.Clamp(seconds, 0, 604800)
            : 1800;

    public string? GetConfigurationError()
    {
        if (!Enabled) return null;
        if (PollIntervalMs < 100) return "DispatchTaskSteps:PollIntervalMs不能小于100ms。";
        if (BatchSize < 1) return "DispatchTaskSteps:BatchSize必须大于0。";
        if (RollingWindowMinimumEdges < 1 ||
            RollingWindowMaximumEdges < RollingWindowMinimumEdges ||
            RollingWindowMaximumEdges > 1000 ||
            RollingWindowHardMaximumEdges is < 0 or > 1000 ||
            RollingWindowHardMaximumEdges > 0 &&
                RollingWindowHardMaximumEdges < RollingWindowMaximumEdges)
            return "DispatchTaskSteps滚动路权窗口边数配置无效。";
        if (!RollingWindowBoundaries.IsKnown(RollingWindowBoundary))
            return "DispatchTaskSteps:RollingWindowBoundary必须是NearestSafeStop或FarthestWithinMaximum。";
        if (PartialWindowMinimumEdges < 1) return "DispatchTaskSteps:PartialWindowMinimumEdges必须大于0。";
        if (SelectAgvTimeoutSeconds is < 0 or > 604800)
            return "DispatchTaskSteps:SelectAgvTimeoutSeconds必须在0到604800之间。";
        if (!TaskTemplateExecutionPolicy.IsKnownTimeoutAction(DefaultTimeoutAction))
            return "DispatchTaskSteps:DefaultTimeoutAction无效。";
        if (DefaultStepTimeoutSeconds.Any(pair => pair.Value is < 0 or > 604800))
            return "DispatchTaskSteps:DefaultStepTimeoutSeconds必须在0到604800之间。";
        return null;
    }
}

public static class RollingWindowBoundaries
{
    public const string NearestSafeStop = "NearestSafeStop";
    public const string FarthestWithinMaximum = "FarthestWithinMaximum";

    public static bool IsKnown(string? value) =>
        string.Equals(value, NearestSafeStop, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, FarthestWithinMaximum, StringComparison.OrdinalIgnoreCase);
}
