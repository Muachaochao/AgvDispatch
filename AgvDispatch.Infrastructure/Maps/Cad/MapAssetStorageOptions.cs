namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapAssetStorageOptions
{
    public string CachePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "map-underlays");
    public string BackupPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "map-backups");
}
