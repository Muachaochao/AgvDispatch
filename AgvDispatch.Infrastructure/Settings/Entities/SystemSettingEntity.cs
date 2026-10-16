using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("system_setting")]
public sealed class SystemSettingEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string Key { get; set; } = string.Empty;

    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? Value { get; set; }

    [SugarColumn(Length = 500, IsNullable = true)]
    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; }
}
