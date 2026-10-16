using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("map_operation_audit")]
public sealed class MapOperationAuditEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string AuditId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)]
    public string MapId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? VersionId { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)]
    public string Operation { get; set; } = string.Empty;
    public bool Success { get; set; }
    [SugarColumn(Length = 256, IsNullable = false)]
    public string OperatorIdentity { get; set; } = string.Empty;
    [SugarColumn(Length = 2000, IsNullable = false)]
    public string Summary { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
}
