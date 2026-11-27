namespace AgvDispatch.Service;

public sealed class DatabaseBackupOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int RetentionDays { get; set; } = 7;
    public int StartupDelaySeconds { get; set; } = 60;
    public long MinimumSizeBytes { get; set; } = 4096;
    public string Directory { get; set; } = "backups";

    public void Validate()
    {
        if (IntervalHours is < 1 or > 168)
            throw new InvalidOperationException("DatabaseBackup:IntervalHours 必须在1～168之间。");
        if (RetentionDays is < 1 or > 3650)
            throw new InvalidOperationException("DatabaseBackup:RetentionDays 必须在1～3650之间。");
        if (StartupDelaySeconds is < 0 or > 3600)
            throw new InvalidOperationException("DatabaseBackup:StartupDelaySeconds 必须在0～3600之间。");
        if (MinimumSizeBytes is < 0 or > 1024L * 1024 * 1024)
            throw new InvalidOperationException("DatabaseBackup:MinimumSizeBytes 必须在0～1GiB之间。");
        if (string.IsNullOrWhiteSpace(Directory) || Directory.Length > 512)
            throw new InvalidOperationException("DatabaseBackup:Directory 不能为空且不能超过512字符。");
    }
}
