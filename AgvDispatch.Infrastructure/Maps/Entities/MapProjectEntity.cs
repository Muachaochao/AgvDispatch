using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("map_project")]
public sealed class MapProjectEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string MapId { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = false)]
    public string Name { get; set; } = string.Empty;
    [SugarColumn(Length = 1000, IsNullable = false)]
    public string Description { get; set; } = string.Empty;
    [SugarColumn(Length = 1000, IsNullable = false)]
    public string BackgroundFilePath { get; set; } = string.Empty;
    [SugarColumn(Length = 32, IsNullable = false)]
    public string BackgroundFormat { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)]
    public string CoordinateSystem { get; set; } = string.Empty;
    [SugarColumn(Length = 16, IsNullable = false)]
    public string Unit { get; set; } = string.Empty;
    public double CoordinateScale { get; set; }
    [SugarColumn(Length = 1000, IsNullable = false)]
    public string PreviewPngPath { get; set; } = string.Empty;
    [SugarColumn(Length = 1000, IsNullable = false)]
    public string PreviewSvgPath { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = false)]
    public string CadLayersJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)]
    public string? CadViewBoxJson { get; set; }
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)]
    public string? CalibrationJson { get; set; }
    public int UnderlayStatus { get; set; }
    [SugarColumn(Length = 2000, IsNullable = false)]
    public string UnderlayMessage { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = false)]
    public string UnderlayIncludedLayersJson { get; set; } = "[]";
    [SugarColumn(IsNullable = true)]
    public int? UnderlayWidth { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? UnderlayHeight { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? UnderlayGeneratedAt { get; set; }
    [SugarColumn(DefaultValue = "0")]
    public bool IsArchived { get; set; }
    public bool IsActive { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ActiveVersionId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? PublishedVersionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
