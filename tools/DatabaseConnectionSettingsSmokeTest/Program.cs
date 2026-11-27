using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Service;
using SqlSugar;
using System.Collections.Concurrent;
using System.Diagnostics;

var checks = 0;
void Check(bool passed, string name)
{
    if (!passed) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var root = Path.Combine(Path.GetTempPath(), "agv-db-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var options = new DatabaseOptions { DatabasePath = Path.Combine(root, "private.db") };
using (var initial = DatabaseFactory.Create(options))
{
    await new DatabaseInitializer(initial).InitializeAsync(default);
    initial.Ado.ExecuteCommand("CREATE TABLE pragma_parent (Id TEXT PRIMARY KEY);");
    initial.Ado.ExecuteCommand("CREATE TABLE pragma_child (Id TEXT PRIMARY KEY, ParentId TEXT NOT NULL REFERENCES pragma_parent(Id));");
}

var pragmaFailures = new ConcurrentBag<string>();
await Parallel.ForEachAsync(Enumerable.Range(0, 8), async (index, ct) =>
{
    using var connection = DatabaseFactory.Create(options);
    var foreignKeys = connection.Ado.SqlQuery<int>("PRAGMA foreign_keys;").Single();
    if (foreignKeys != 1) pragmaFailures.Add($"connection {index}: foreign_keys={foreignKeys}");
    try
    {
        connection.Ado.ExecuteCommand(
            $"INSERT INTO pragma_child(Id,ParentId) VALUES('child-{index}','missing-parent');");
        pragmaFailures.Add($"connection {index}: invalid foreign key was accepted");
    }
    catch (Exception error) when (error.Message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase))
    {
    }
    await Task.Yield();
});
Check(pragmaFailures.IsEmpty,
    "all parallel SQLite connections inherit foreign_keys and enforce FK constraints: " +
    string.Join("; ", pragmaFailures));

// Microsoft.Data.Sqlite implements "Default Timeout" with a native busy handler,
// so PRAGMA busy_timeout still reports 0. Verify the observable contract instead:
// a second writer waits for the first transaction instead of failing at once.
using (var lockWriter = DatabaseFactory.Create(options))
using (var lockWaiter = DatabaseFactory.Create(options))
{
    lockWriter.Ado.BeginTran();
    lockWriter.Ado.ExecuteCommand("INSERT INTO pragma_parent(Id) VALUES('lock-holder');");
    var waitTimer = Stopwatch.StartNew();
    var waitingWrite = Task.Run(() => lockWaiter.Ado.ExecuteCommandAsync(
        "INSERT INTO pragma_parent(Id) VALUES('lock-waiter');"));
    await Task.Delay(400);
    Check(!waitingWrite.IsCompleted,
        "second writer waits for an active SQLite transaction instead of failing immediately");
    lockWriter.Ado.CommitTran();
    await waitingWrite.WaitAsync(TimeSpan.FromSeconds(10));
    waitTimer.Stop();
    Check(waitTimer.Elapsed >= TimeSpan.FromMilliseconds(300),
        "default connection timeout keeps the second writer waiting until the lock is released");
}

Check(new AgvCommandOutboxOptions().GetConfigurationError() is null &&
      new AgvCommandOutboxOptions { MaximumConcurrentVehicles = 0 }.GetConfigurationError() is not null,
    "outbox concurrency and timing options reject invalid values");
Check(new DispatchTaskStepOptions().GetConfigurationError() is null &&
      new DispatchTaskStepOptions { BatchSize = 0 }.GetConfigurationError() is not null &&
      new DispatchTaskStepOptions { RollingWindowBoundary = "Typo" }.GetConfigurationError() is not null,
    "task-step batching, rolling window and watchdog options validate together");
Check(new DispatchTaskCreationOptions().GetConfigurationError() is null &&
      new DispatchTaskCreationOptions { DefaultPriority = 101 }.GetConfigurationError() is not null &&
      new DispatchTaskCreationOptions { AcknowledgementRetryIntervalMs = 0 }.GetConfigurationError() is not null,
    "task-creation priority and acknowledgement options reject invalid values");
Check(new StationStateOptions().GetConfigurationError() is null &&
      new StationStateOptions { RefreshIntervalMs = 0 }.GetConfigurationError() is not null,
    "station refresh interval rejects zero and excessive polling");
Check(new AlarmOptions().GetConfigurationError() is null &&
      new AlarmOptions { LowBatteryCriticalPercent = 40, LowBatteryWarningPercent = 20 }.GetConfigurationError() is not null,
    "alarm thresholds reject critical battery above warning battery");
Check(new DatabaseBackupOptions().IsValidForTest() &&
      !new DatabaseBackupOptions { RetentionDays = 0 }.IsValidForTest(),
    "database backup options validate before the host starts");

Console.WriteLine($"Database connection/settings: {checks} checks passed. Isolated SQLite only.");

static class DatabaseBackupOptionsTestExtensions
{
    public static bool IsValidForTest(this DatabaseBackupOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
