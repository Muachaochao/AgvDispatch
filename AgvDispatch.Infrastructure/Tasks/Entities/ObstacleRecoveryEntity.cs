using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("obstacle_recovery")]
public sealed class ObstacleRecoveryEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string RecoveryId { get; set; } = "";

    [SugarColumn(Length = 64)]
    public string TaskId { get; set; } = "";

    [SugarColumn(Length = 64)]
    public string StepId { get; set; } = "";

    [SugarColumn(Length = 32)]
    public string VehicleId { get; set; } = "";

    [SugarColumn(Length = 128)]
    public string OriginalVendorTaskId { get; set; } = "";

    [SugarColumn(Length = 64)]
    public string State { get; set; } = "";

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Message { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? NextAction { get; set; }

    public DateTime BlockedSinceUtc { get; set; }

    [SugarColumn(ColumnDataType = "TEXT")]
    public string OriginalPathJson { get; set; } = "[]";

    [SugarColumn(ColumnDataType = "TEXT")]
    public string CandidatePathJson { get; set; } = "[]";

    [SugarColumn(ColumnDataType = "TEXT")]
    public string BlockedEdgeIdsJson { get; set; } = "[]";

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SafeSite { get; set; }

    [SugarColumn(Length = 64)]
    public string MapId { get; set; } = "";

    [SugarColumn(Length = 64)]
    public string MapVersionId { get; set; } = "";

    [SugarColumn(Length = 64)]
    public string DefinitionHash { get; set; } = "";

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? NewVendorTaskId { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CancelRequestedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? OriginalTerminalAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? SendAttemptedAtUtc { get; set; }

    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
