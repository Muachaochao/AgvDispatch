using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("execution_recovery_session")]
public sealed class ExecutionRecoverySessionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string SessionId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public bool Released { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ReleasedAt { get; set; }
}
[SugarTable("execution_recovery_ticket")]
public sealed class ExecutionRecoveryTicketEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string TicketId { get; set; } = "";
    [SugarColumn(Length = 64)] public string SessionId { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 64)] public string StepId { get; set; } = "";
    public bool Verified { get; set; }
    public int Revision { get; set; }
    [SugarColumn(ColumnDataType = "TEXT")] public string Reason { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string Evidence { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string Fingerprint { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string PhysicalFingerprint { get; set; } = "";
    [SugarColumn(IsNullable = true)] public DateTime? VerifiedAt { get; set; }
    [SugarColumn(IsNullable = true, Length = 128)] public string? MaterialReference { get; set; }
    public bool OfflineVehicleConfirmed { get; set; }
}
[SugarTable("execution_evidence")]
public sealed class ExecutionEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string Id { get; set; } = "";
    [SugarColumn(Length = 64)] public string SessionId { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 256)] public string Operator { get; set; } = "";
    [SugarColumn(Length = 64)] public string Kind { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string Reason { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string Evidence { get; set; } = "";
    public DateTime OccurredAt { get; set; }
}
