using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("alarm_record")]
public sealed class AlarmRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string AlarmId { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string RuleKey { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string SourceType { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string SourceId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string Code { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(Length = 2000, IsNullable = false)]
    public string Message { get; set; } = string.Empty;

    public int Severity { get; set; }
    public int Status { get; set; }
    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastObservedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? AcknowledgedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? AcknowledgedAt { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? AcknowledgeComment { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? RecoveredAt { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? RecoveryMessage { get; set; }
}
