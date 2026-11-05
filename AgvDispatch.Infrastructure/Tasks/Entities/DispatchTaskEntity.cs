using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("dispatch_task")]
public sealed class DispatchTaskEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string TaskId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string StationId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = true)]
    public string? VehicleId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? VendorTaskId { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)] public string? CancelRequestId { get; set; }
    [SugarColumn(IsNullable = true)] public int? CancelOriginalStage { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? CancelStepId { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)] public string? CancelVendorId { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)] public string? CancelVehicleId { get; set; }
    [SugarColumn(Length = 1000, IsNullable = true)] public string? CancelReason { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)] public string? CancelRequestedBy { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CancelRequestedAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CancelNextAttemptAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CancelSentAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CancelConfirmedAt { get; set; }
    [SugarColumn(IsNullable = true)] public int? CancelAttemptCount { get; set; }
    public bool CancelRequiresPickupNavigation { get; set; }
    public int Stage { get; set; }
    public int Priority { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? RequestedVehicleId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreateRequestId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreateMapId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreateMapVersionId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreateParameterRevision { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "TEXT")]
    public string? TaskTemplateJson { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "TEXT")]
    public string? DependsOnTaskIdsJson { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? PauseRequestId { get; set; }
    [SugarColumn(IsNullable = true)] public int? PauseOriginalStage { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? PauseStepId { get; set; }
    [SugarColumn(Length = 1000, IsNullable = true)] public string? PauseReason { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)] public string? PauseRequestedBy { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? PauseRequestedAt { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? PauseResumeRequestId { get; set; }
    [SugarColumn(Length = 1000, IsNullable = true)] public string? PauseResumeReason { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)] public string? PauseResumeRequestedBy { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? PauseResumedAt { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ErrorMessage { get; set; }
}
