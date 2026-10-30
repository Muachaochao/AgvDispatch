using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("traffic_road_closure")]
public sealed class RoadClosureEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string ClosureId { get; set; } = "";
    [SugarColumn(Length = 128)] public string RequestId { get; set; } = "";
    [SugarColumn(Length = 64)] public string IntentHash { get; set; } = "";
    public string MapId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string DefinitionHash { get; set; } = "";
    public string EdgeId { get; set; } = "";
    public string FromNodeId { get; set; } = "";
    public string ToNodeId { get; set; } = "";
    public bool Bidirectional { get; set; }
    [SugarColumn(ColumnDataType = "TEXT")] public string TraversalsJson { get; set; } = "[]";
    public string Source { get; set; } = "";
    public string Reason { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public long CreatedRevision { get; set; }
    public bool Active { get; set; }
    [SugarColumn(IsNullable = true)] public string? ReleasedBy { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ReleasedAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public string? ReleaseReason { get; set; }
    [SugarColumn(IsNullable = true)] public long? ReleasedRevision { get; set; }
}

[SugarTable("traffic_road_closure_event")]
public sealed class RoadClosureEventEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string EventId { get; set; } = "";
    public string ClosureId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
    public long Revision { get; set; }
}
