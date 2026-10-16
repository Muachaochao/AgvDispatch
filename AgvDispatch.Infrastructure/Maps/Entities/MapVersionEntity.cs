using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("map_version")]
public sealed class MapVersionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string VersionId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)]
    public string MapId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public int Status { get; set; }
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = false)]
    public string DefinitionJson { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = false)]
    public string ValidationJson { get; set; } = string.Empty;
    [SugarColumn(Length = 256, IsNullable = false)]
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    [SugarColumn(Length = 36, IsNullable = false, DefaultValue = "")]
    public string EditToken { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public DateTime? PublishedAt { get; set; }
}
