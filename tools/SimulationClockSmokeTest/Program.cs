using AgvDispatch.Application.Runtime;
using AgvDispatch.Connectors.Plc;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS " + message);
}

void ExpectFailure(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        checks++;
        Console.WriteLine("PASS " + message);
        return;
    }
    throw new InvalidOperationException(message);
}

var names = new[]
{
    "AGV_SIMULATION_RATE",
    "AGV_SIMULATION_ORIGIN_UNIX_MS",
    "AGV_SIMULATION_ROOT",
    "AGV_DISPATCH_DATABASE_PATH"
};
var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
var originalClock = BusinessClock.Provider;
try
{
    foreach (var name in names)
        Environment.SetEnvironmentVariable(name, null);
    Check(SimulationTimeProvider.FromEnvironment().Rate == 1,
        "without simulation settings the service keeps the real-time clock");

    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        "agv-simulation-clock-" + Guid.NewGuid().ToString("N")));
    var database = Path.Combine(root, "data", "agv-dispatch.db");
    var origin = DateTimeOffset.UtcNow.AddSeconds(-2);
    Environment.SetEnvironmentVariable("AGV_SIMULATION_RATE", "4");
    Environment.SetEnvironmentVariable("AGV_SIMULATION_ORIGIN_UNIX_MS",
        origin.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    Environment.SetEnvironmentVariable("AGV_SIMULATION_ROOT", root);
    Environment.SetEnvironmentVariable("AGV_DISPATCH_DATABASE_PATH", database);
    var isolated = SimulationTimeProvider.FromEnvironment();
    Check(isolated.Rate == 4, "isolated instances accept the configured business-time rate");
    var accelerated = isolated.GetUtcNow();
    Check(accelerated >= DateTimeOffset.UtcNow.AddSeconds(5) &&
          accelerated <= DateTimeOffset.UtcNow.AddSeconds(10),
        "late-starting components align to the shared accelerated wall origin");
    Check(isolated.ScaleForBusiness(TimeSpan.FromSeconds(8)) == TimeSpan.FromSeconds(2),
        "business waits and collection periods scale with the selected rate");

    var jumping = new JumpClock(DateTimeOffset.UtcNow);
    var monotonic = new MonotonicTimeProvider(jumping);
    var beforeRollback = monotonic.GetUtcNow();
    jumping.Now = beforeRollback.AddHours(-2);
    Thread.Sleep(30);
    var afterRollback = monotonic.GetUtcNow();
    Check(afterRollback >= beforeRollback &&
          afterRollback < beforeRollback.AddMinutes(1),
        "system clock rollback cannot move business freshness backwards");
    jumping.Now = beforeRollback.AddHours(2);
    Check(monotonic.GetUtcNow() >= beforeRollback.AddHours(2),
        "forward wall-clock jumps remain visible to freshness checks");

    BusinessClock.Use(isolated);
    var plc = new PlcConnectionSupervisor(new PlcOptions());
    var beforeRead = isolated.GetUtcNow();
    plc.ReportSuccess(TimeSpan.FromMilliseconds(12));
    Check(plc.Current.LastSuccessfulReadAt >= beforeRead && plc.Current.LastSuccessfulReadAt <= isolated.GetUtcNow(),
        "PLC feedback timestamps use the business clock expected by the startup gate");

    Environment.SetEnvironmentVariable("AGV_DISPATCH_DATABASE_PATH",
        Path.Combine(Path.GetTempPath(), "outside-simulation.db"));
    ExpectFailure(() => SimulationTimeProvider.FromEnvironment(),
        "accelerated clocks cannot escape into a non-isolated database");
}
finally
{
    BusinessClock.Use(originalClock);
    foreach (var entry in previous)
        Environment.SetEnvironmentVariable(entry.Key, entry.Value);
}

Console.WriteLine($"Simulation clock smoke test passed ({checks} checks).");

sealed class JumpClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
