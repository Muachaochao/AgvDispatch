using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public static class DatabaseFactory
{
    public static ISqlSugarClient Create(DatabaseOptions options)
    {
        var fullPath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        return new SqlSugarScope(new ConnectionConfig
        {
            // All long-lived workers share one SQLite file. A finite default
            // timeout lets short, independent transactions queue instead of
            // failing immediately when another evidence write is committing.
            // These settings are connection-scoped in SQLite. Keep them in the
            // factory so every pooled connection (workers, HTTP readers and
            // one-off diagnostics) inherits the same lock and FK behavior;
            // DatabaseInitializer still sets WAL/synchronous once per file.
            ConnectionString = $"DataSource={fullPath};Default Timeout=30;Foreign Keys=True;Pooling=True",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                SqliteCodeFirstEnableDefaultValue = true
            }
        });
    }

    /// <summary>
    /// Creates a single-connection client with the same schema settings as the
    /// shared scope. Used for work that must own its transaction across async
    /// continuations without sharing SqlSugarScope state with parallel workers.
    /// </summary>
    public static ISqlSugarClient CreateIsolated(ISqlSugarClient database)
    {
        var source = database.CurrentConnectionConfig;
        return new SqlSugarClient(new ConnectionConfig
        {
            ConfigId = source.ConfigId,
            DbType = source.DbType,
            ConnectionString = source.ConnectionString,
            IsAutoCloseConnection = false,
            InitKeyType = source.InitKeyType,
            MoreSettings = source.MoreSettings,
            ConfigureExternalServices = source.ConfigureExternalServices,
            SlaveConnectionConfigs = source.SlaveConnectionConfigs,
            AopEvents = source.AopEvents,
            SqlMiddle = source.SqlMiddle
        });
    }
}
