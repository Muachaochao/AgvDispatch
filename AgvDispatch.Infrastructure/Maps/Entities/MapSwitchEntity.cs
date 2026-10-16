using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("map_switch")]
public sealed class MapSwitchEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string ReleaseId { get; set; } = "";
    [SugarColumn(Length = 64)] public string MapId { get; set; } = "";
    [SugarColumn(Length = 64)] public string VersionId { get; set; } = "";
    [SugarColumn(IsNullable = true, Length = 64)] public string? PreviousMapId { get; set; }
    [SugarColumn(IsNullable = true, Length = 64)] public string? PreviousVersionId { get; set; }
    public int Phase { get; set; }
    [SugarColumn(Length = 256)] public string OperatorIdentity { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string Evidence { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
