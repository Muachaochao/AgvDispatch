using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("task_equipment_claim")]
public sealed class TaskEquipmentClaimEntity
{
    [SugarColumn(IsPrimaryKey=true,Length=128)] public string EquipmentId { get; set; }="";
    [SugarColumn(Length=64)] public string TaskId { get; set; }="";
    public DateTime AcquiredAtUtc { get; set; }
}

[SugarTable("task_handshake_checkpoint")]
public sealed class TaskHandshakeEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 64)] public string CycleId { get; set; } = "";
    [SugarColumn(Length = 64)] public string ContractVersion { get; set; } = "";
    [SugarColumn(Length = 96)] public string Phase { get; set; } = "";
    public int Revision { get; set; }
    [SugarColumn(ColumnDataType = "TEXT")] public string Reason { get; set; } = "";
    [SugarColumn(IsNullable = true, Length = 128)] public string? PendingSignal { get; set; }
    [SugarColumn(IsNullable = true)] public bool? PendingValue { get; set; }
    [SugarColumn(IsNullable = true, Length = 96)] public string? NextPhase { get; set; }
    [SugarColumn(ColumnDataType = "TEXT")] public string BaselineJson { get; set; } = "[]";
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[SugarTable("task_runtime_event")]
public sealed class TaskRuntimeEventEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")] public long Sequence { get; set; }
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 32)] public string Kind { get; set; } = "";
    [SugarColumn(Length = 96)] public string Phase { get; set; } = "";
    [SugarColumn(Length = 128)] public string StationId { get; set; } = "";
    [SugarColumn(Length = 128)] public string Signal { get; set; } = "";
    [SugarColumn(IsNullable = true)] public bool? PreviousValue { get; set; }
    [SugarColumn(IsNullable = true)] public bool? Value { get; set; }
    [SugarColumn(Length = 32)] public string Quality { get; set; } = "";
    [SugarColumn(IsNullable = true)] public DateTime? ObservedAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    [SugarColumn(ColumnDataType = "TEXT")] public string Reason { get; set; } = "";
    [SugarColumn(IsNullable=true,ColumnDataType = "TEXT")] public string? EvidenceJson { get; set; }
}

[SugarTable("task_signal_last_observation")]
public sealed class TaskSignalLastEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string Key { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 128)] public string StationId { get; set; } = "";
    [SugarColumn(Length = 128)] public string Signal { get; set; } = "";
    [SugarColumn(IsNullable = true)] public bool? Value { get; set; }
    [SugarColumn(Length = 32)] public string Quality { get; set; } = "";
    [SugarColumn(Length = 128)] public string Address { get; set; } = "";
}
