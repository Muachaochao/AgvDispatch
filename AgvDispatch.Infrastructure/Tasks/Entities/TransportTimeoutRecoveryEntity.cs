using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("transport_timeout_recovery")]
public sealed class TransportTimeoutRecoveryEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string RecoveryId { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 32)] public string VehicleId { get; set; } = "";
    public int StepType { get; set; }
    [SugarColumn(Length = 64)] public string State { get; set; } = "";
    [SugarColumn(Length = 64)] public string Outcome { get; set; } = "";
    [SugarColumn(Length = 128)] public string OriginalVendorTaskId { get; set; } = "";
    [SugarColumn(Length = 128, IsNullable = true)] public string? NewVendorTaskId { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)] public string? CurrentStation { get; set; }
    [SugarColumn(Length = 128)] public string DestinationSite { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string OriginalPathJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "TEXT")] public string CandidatePathJson { get; set; } = "[]";
    [SugarColumn(Length = 64)] public string MapId { get; set; } = "";
    [SugarColumn(Length = 64)] public string MapVersionId { get; set; } = "";
    [SugarColumn(Length = 64)] public string DefinitionHash { get; set; } = "";
    [SugarColumn(Length = 64)] public string PreviewHash { get; set; } = "";
    [SugarColumn(IsNullable = true)] public DateTime? SendAttemptedAt { get; set; }
    [SugarColumn(Length = 1000, IsNullable = true)] public string? LastError { get; set; }
    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
