using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("dispatch_task_step")]
public sealed class DispatchTaskStepEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string StepId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TaskId { get; set; } = string.Empty;

    public int Sequence { get; set; }
    public int StepType { get; set; }
    public int Status { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceSite { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? DestinationSite { get; set; }

    [SugarColumn(Length = 32, IsNullable = true)]
    public string? VehicleId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? VendorTaskId { get; set; }

    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? StartedAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CompletedAt { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ErrorMessage { get; set; }
}
