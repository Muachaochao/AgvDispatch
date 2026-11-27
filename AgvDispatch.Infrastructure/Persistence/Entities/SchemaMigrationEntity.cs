using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("schema_migration")]
public sealed class SchemaMigrationEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Version { get; set; }

    public DateTime AppliedAtUtc { get; set; }

    [SugarColumn(Length = 64)]
    public string ApplicationVersion { get; set; } = string.Empty;
}
