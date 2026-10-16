using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("agv_command_outbox")]
public sealed class AgvCommandOutboxEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string CommandId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TaskId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string StepId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string VehicleId { get; set; } = string.Empty;

    public int CommandType { get; set; }
    public int Status { get; set; }

    [SugarColumn(ColumnDataType = "TEXT", IsNullable = false)]
    public string PayloadJson { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? ExecutionMapId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? ExecutionMapVersionId { get; set; }
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)] public string? ExecutionRouteSnapshotJson { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? ExecutionRouteSnapshotHash { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ClaimedAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? SentAt { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? VendorCommandId { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? LastError { get; set; }
}
