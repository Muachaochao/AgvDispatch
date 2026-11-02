using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("task_switch_session")]
public sealed class TaskSwitchEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 32)] public string SwitchId { get; set; } = "";
    [SugarColumn(Length = 64)] public string SourceTaskId { get; set; } = "";
    [SugarColumn(Length = 32)] public string VehicleId { get; set; } = "";
    [SugarColumn(Length = 32)] public string TargetStationId { get; set; } = "";
    [SugarColumn(Length = 64, IsNullable = true)] public string? TargetTaskId { get; set; }
    [SugarColumn(Length = 32)] public string Stage { get; set; } = "";
    [SugarColumn(Length = 32, IsNullable = true)] public string? ResumeStage { get; set; }
    [SugarColumn(ColumnDataType = "TEXT")] public string Message { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string BlockersJson { get; set; } = "[]";
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    [SugarColumn(Length = 64)] public string MapId { get; set; } = "";
    [SugarColumn(Length = 64)] public string MapVersionId { get; set; } = "";
    [SugarColumn(Length = 128)] public string ParameterRevision { get; set; } = "";
    [SugarColumn(Length = 32)] public string ReplacementRequestId { get; set; } = "";
    [SugarColumn(Length = 32)] public string CancelRequestId { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string RequestJson { get; set; } = "";
    [SugarColumn(Length = 256)] public string Operator { get; set; } = "";
    public bool ReplacementCreateAttempted { get; set; }
}
