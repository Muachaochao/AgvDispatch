using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string text)
    {
        if (!condition) throw new Exception(text);
        _checks++;
        Console.WriteLine("PASS " + text);
    }

    private static async Task<int> Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "agv-db-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "agv-dispatch.db");
        var databaseOptions = new DatabaseOptions { DatabasePath = source };
        using var database = DatabaseFactory.Create(databaseOptions);
        await new DatabaseInitializer(database).InitializeAsync(default);
        Check(database.Ado.SqlQuery<int>("PRAGMA user_version;").Single() == DatabaseSchemaVersion.Current,
            "旧库初始化后写入当前架构版本");
        Check(database.Queryable<SchemaMigrationEntity>()
            .Any(row => row.Version == DatabaseSchemaVersion.Current),
            "架构迁移记录已持久化");
        await database.Insertable(new SystemSettingEntity
        {
            Key = "backup-proof",
            Value = "present",
            UpdatedAt = DateTime.UtcNow
        }).ExecuteCommandAsync();

        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new DatabaseBackupOptions
        {
            Enabled = true,
            IntervalHours = 24,
            RetentionDays = 1,
            StartupDelaySeconds = 0,
            MinimumSizeBytes = 1,
            Directory = "backups"
        };
        var service = new DatabaseBackupService(
            new ConsoleLogger<DatabaseBackupService>(), database, databaseOptions, options, clock);

        var first = await service.BackupOnceAsync(default);
        Check(File.Exists(first), "第一次在线备份已生成");
        Check(new FileInfo(first).Length > 0, "备份文件非空");

        clock.Now = clock.Now.AddDays(2);
        var second = await service.BackupOnceAsync(default);
        Check(File.Exists(second) && !string.Equals(first, second, StringComparison.OrdinalIgnoreCase),
            "第二次备份使用新的时间戳文件");
        Check(!File.Exists(first), "超过保留天数的旧备份已删除");
        using (var copy = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = second }))
            Check(await copy.Queryable<SystemSettingEntity>()
                .Where(row => row.Key == "backup-proof").AnyAsync(),
                "备份数据库包含源库已提交的数据");

        var leftovers = Directory.GetFiles(Path.Combine(root, "backups"), "*.tmp");
        Check(leftovers.Length == 0, "成功备份后没有遗留临时文件");

        await database.Ado.ExecuteCommandAsync("PRAGMA user_version=999;");
        var rejected = false;
        try { await new DatabaseInitializer(database).InitializeAsync(default); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "遇到高于程序支持的架构版本时拒绝启动");

        Console.WriteLine($"PASS: {_checks} database backup checks; isolated SQLite only.");
        return 0;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ConsoleLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)} {exception?.Message}");
    }
}
