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
            ConnectionString = $"DataSource={fullPath}",
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
