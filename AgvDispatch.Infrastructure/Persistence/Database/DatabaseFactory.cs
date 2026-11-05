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
            ConnectionString = $"DataSource={fullPath};Default Timeout=30",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                SqliteCodeFirstEnableDefaultValue = true
            }
        });
    }
}
