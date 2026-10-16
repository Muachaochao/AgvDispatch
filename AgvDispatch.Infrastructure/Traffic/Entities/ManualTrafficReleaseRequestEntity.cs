using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("manual_traffic_release_request")]
public sealed class ManualTrafficReleaseRequestEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string RequestId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string LeaseId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ResourceId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TaskId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string VehicleId { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string RequestedBy { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string RequestedByDisplayName { get; set; } = string.Empty;

    [SugarColumn(Length = 1000, IsNullable = false)]
    public string Reason { get; set; } = string.Empty;

    public bool VehicleClearConfirmed { get; set; }

    [SugarColumn(Length = 256, IsNullable = false)]
    public string ConfirmationText { get; set; } = string.Empty;

    public int Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? ReviewedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReviewedAt { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ReviewComment { get; set; }
}
