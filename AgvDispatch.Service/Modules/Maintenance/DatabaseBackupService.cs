using AgvDispatch.Application.Runtime;
using AgvDispatch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using SqlSugar;
using System.Globalization;

namespace AgvDispatch.Service;

/// <summary>
/// Creates a consistent SQLite snapshot with VACUUM INTO, verifies that the
/// snapshot opens, then atomically renames it into the retention directory.
/// The backup is taken through the existing connection, so it does not copy a
/// WAL file or depend on the process being stopped.
/// </summary>
public sealed class DatabaseBackupService(
    ILogger<DatabaseBackupService> logger,
    ISqlSugarClient database,
    DatabaseOptions databaseOptions,
    DatabaseBackupOptions options,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? BusinessClock.Provider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("数据库定期备份未启用。");
            return;
        }

        options.Validate();
        if (options.StartupDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(options.StartupDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Due())
                    await BackupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "数据库定期备份失败；保留现有数据库和旧备份。");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    public async Task<string> BackupOnceAsync(CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(databaseOptions.DatabasePath);
        var directory = ResolveDirectory(source);
        Directory.CreateDirectory(directory);
        var stamp = _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff");
        var finalPath = Path.Combine(directory, $"agv-dispatch-{stamp}.db");
        var temporary = finalPath + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        try
        {
            var escaped = temporary.Replace("'", "''", StringComparison.Ordinal);
            await database.Ado.ExecuteCommandAsync($"VACUUM INTO '{escaped}'");
            var info = new FileInfo(temporary);
            if (!info.Exists || info.Length < options.MinimumSizeBytes)
                throw new InvalidDataException(
                    $"备份文件缺失或小于最小大小（{(info.Exists ? info.Length : 0)} < {options.MinimumSizeBytes}）。");

            File.Move(temporary, finalPath, overwrite: false);
            // Opening the snapshot is the cheapest proof that it is a real
            // SQLite database rather than a truncated copy. No migration or
            // write is performed against it. Use a non-pooled read-only
            // connection so the verified backup can later be pruned; the
            // normal SqlSugar connection pool would keep a Windows file handle.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = finalPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using (var check = new SqliteConnection(connectionString))
            {
                await check.OpenAsync(cancellationToken);
                await using var command = check.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM system_setting;";
                var settingCount = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
                if (settingCount < 0) throw new InvalidDataException("备份数据库无法读取设置表。");
            }

            Prune(directory, finalPath);
            logger.LogInformation("数据库备份完成。Path={Path}, Bytes={Bytes}", finalPath, info.Length);
            return finalPath;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(finalPath)) File.Delete(finalPath);
            }
            catch (IOException) { /* leave the .tmp for the next attempt */ }
            throw;
        }
    }

    private bool Due()
    {
        var directory = ResolveDirectory(Path.GetFullPath(databaseOptions.DatabasePath));
        if (!Directory.Exists(directory)) return true;
        var latest = Directory.EnumerateFiles(directory, "agv-dispatch-*.db")
            .Select(path => (Path: path, Stamp: StampOf(path)))
            .Where(item => item.Stamp is not null)
            .OrderByDescending(item => item.Stamp)
            .FirstOrDefault();
        return latest.Stamp is null ||
            _clock.GetUtcNow() - latest.Stamp.Value >=
                TimeSpan.FromHours(options.IntervalHours);
    }

    private void Prune(string directory, string justWritten)
    {
        var cutoff = _clock.GetUtcNow().AddDays(-options.RetentionDays).UtcDateTime;
        var stale = Directory.EnumerateFiles(directory, "agv-dispatch-*.db")
            .Where(path => !string.Equals(path, justWritten, StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Stamp: StampOf(path)))
            .Where(item => item.Stamp is not null && item.Stamp.Value.UtcDateTime < cutoff)
            .OrderBy(item => item.Stamp)
            .ToArray();
        foreach (var (path, _) in stale)
        {
            try
            {
                File.Delete(path);
                logger.LogInformation("已删除过期数据库备份。Path={Path}", path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "删除过期数据库备份失败。Path={Path}", path);
            }
        }
    }

    private static DateTimeOffset? StampOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "agv-dispatch-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return DateTimeOffset.TryParseExact(name[prefix.Length..], "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var stamp)
            ? stamp
            : null;
    }

    private string ResolveDirectory(string source) =>
        Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(Path.GetDirectoryName(source)!, options.Directory);
}
