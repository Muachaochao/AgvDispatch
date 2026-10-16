using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("traffic_resource_lease")]
public sealed class TrafficResourceLeaseEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string LeaseId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ResourceId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string OwnerStepId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TaskId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string VehicleId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)] public string? MapId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? MapVersionId { get; set; }

    public DateTime AcquiredAt { get; set; }
    public DateTime RenewedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
