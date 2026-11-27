using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence;

var root = Path.Combine(Path.GetTempPath(), "agv-alarm-lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var database = DatabaseFactory.Create(new DatabaseOptions
    {
        DatabasePath = Path.Combine(root, "alarm.db")
    });
    var initializer = new DatabaseInitializer(database);
    await initializer.InitializeAsync(default);
    var alarms = new AlarmService(database);
    var policy = new AlarmSynchronizationPolicy(10);
    var started = DateTimeOffset.UtcNow;
    var critical = new AlarmCandidate(
        "AGV:V1:OBSTACLE_RECOVERY", "AGV", "V1", "AGV_OBSTACLE_RECOVERY",
        "V1自动改道结果待核对", "结果不确定，禁止重发。", AlarmSeverity.Critical);

    await alarms.SynchronizeAsync([critical], started, default, policy);
    await alarms.SynchronizeAsync([], started.AddSeconds(1), default, policy);
    var held = await alarms.GetRecentAsync(10, true, default);
    Require(held.Count == 1 && held[0].Status == AlarmLifecycleStatus.Open,
        "未被确认的严重报警不能在下一周期立刻消失");

    await alarms.SynchronizeAsync([], started.AddSeconds(11), default, policy);
    var recovered = await alarms.GetRecentAsync(10, true, default);
    Require(recovered.Count == 0, "超过保持时间后严重报警应自动恢复");
    var history = await alarms.GetRecentAsync(10, false, default);
    Require(history.Single().Status == AlarmLifecycleStatus.Recovered,
        "恢复历史必须保留");
    Console.WriteLine("PASS: critical alarm hold and recovery lifecycle.");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
