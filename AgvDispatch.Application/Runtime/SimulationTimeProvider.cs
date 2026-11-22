namespace AgvDispatch.Application.Runtime;

public static class BusinessClock
{
    public static TimeProvider Provider { get; private set; } = TimeProvider.System;

    public static void Use(TimeProvider provider) =>
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
}

/// <summary>
/// Shared business clock for an explicitly isolated simulation instance.
/// Network and process watchdogs continue to use wall-clock time.
/// </summary>
public sealed class SimulationTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _originUtc;

    private SimulationTimeProvider(double rate, DateTimeOffset originUtc)
    {
        if (!double.IsFinite(rate) || rate is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(rate), "仿真倍率必须在 1 至 8 之间。");
        Rate = rate;
        _originUtc = originUtc.ToUniversalTime();
    }

    public double Rate { get; }

    public override DateTimeOffset GetUtcNow()
    {
        // The origin is shared by the controller, PLC, vehicles and service.
        // A process-local stopwatch would make late-starting children lag the
        // controller by their startup time multiplied by the simulation rate.
        var elapsed = DateTimeOffset.UtcNow - _originUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        return _originUtc + elapsed * Rate;
    }

    public TimeSpan ScaleForBusiness(TimeSpan wallTime) =>
        TimeSpan.FromTicks(checked((long)Math.Ceiling(wallTime.Ticks / Rate)));

    public static SimulationTimeProvider FromEnvironment()
    {
        var rawRate = Environment.GetEnvironmentVariable("AGV_SIMULATION_RATE");
        if (string.IsNullOrWhiteSpace(rawRate))
            return new(1, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow());

        if (!double.TryParse(rawRate, global::System.Globalization.NumberStyles.Float,
                global::System.Globalization.CultureInfo.InvariantCulture, out var rate) ||
            !double.IsFinite(rate) || rate is < 1 or > 8)
            throw new InvalidOperationException("AGV_SIMULATION_RATE 必须是 1 至 8 的有限数值。");

        var root = Environment.GetEnvironmentVariable("AGV_SIMULATION_ROOT");
        var database = Environment.GetEnvironmentVariable("AGV_DISPATCH_DATABASE_PATH");
        if (rate > 1)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("运行倍率只允许在显式隔离的仿真实例中启用。");
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullDatabase = Path.GetFullPath(database);
            if (!fullDatabase.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("隔离仿真的数据库必须位于 AGV_SIMULATION_ROOT 内。");
        }

        var rawOrigin = Environment.GetEnvironmentVariable("AGV_SIMULATION_ORIGIN_UNIX_MS");
        if (!long.TryParse(rawOrigin, out var unixMilliseconds))
            unixMilliseconds = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().ToUnixTimeMilliseconds();
        return new(rate, DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds));
    }
}
