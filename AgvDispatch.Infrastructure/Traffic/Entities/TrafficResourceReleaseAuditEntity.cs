using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("traffic_resource_release_audit")]
public sealed class TrafficResourceReleaseAuditEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string AuditId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string LeaseId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ResourceId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string OwnerStepId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TaskId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string VehicleId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string Operator { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? RequestedBy { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? ApprovedBy { get; set; }

    [SugarColumn(Length = 1000, IsNullable = false)]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public int? PreviousTaskStage { get; set; }

    public bool HeartbeatExpired { get; set; }
    public DateTime AcquiredAt { get; set; }
    public DateTime RenewedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime ReleasedAt { get; set; }
}
