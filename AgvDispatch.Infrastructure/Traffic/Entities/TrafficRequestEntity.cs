using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence.Entities;
[SugarTable("traffic_request")]
public sealed class TrafficRequestEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 32)] public string VehicleId { get; set; } = "";
    [SugarColumn(Length = 128)] public string Source { get; set; } = "";
    [SugarColumn(Length = 128)] public string Destination { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string EdgeIds { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "TEXT")] public string ResourceIds { get; set; } = "[]";
    [SugarColumn(Length = 32)] public string State { get; set; } = "";
    [SugarColumn(Length = 4000)] public string Reason { get; set; } = "";
    [SugarColumn(Length = 32, IsNullable = true)] public string? WaitingCategory { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? MapId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? MapVersionId { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? WaitingSinceUtc { get; set; }
    [SugarColumn(IsNullable = true)] public bool? WaitingStartKnown { get; set; }
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)] public string? BlockingKey { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? BlockingSinceUtc { get; set; }
    [SugarColumn(IsNullable = true)] public bool? BlockingStartKnown { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? LastBlockingClearedUtc { get; set; }
    [SugarColumn(IsNullable = true)] public double? QueuePriority { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? QueueObservedAtUtc { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
[SugarTable("traffic_lifecycle_event")]
public sealed class TrafficLifecycleEventEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string EventId { get; set; } = "";
    [SugarColumn(Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 32)] public string VehicleId { get; set; } = "";
    [SugarColumn(Length = 32)] public string Kind { get; set; } = "";
    [SugarColumn(Length = 4000)] public string Reason { get; set; } = "";
    [SugarColumn(ColumnDataType = "TEXT")] public string ResourceIds { get; set; } = "[]";
    [SugarColumn(IsNullable = true)] public DateTime? WaitingSinceUtc { get; set; }
    [SugarColumn(IsNullable = true)] public bool? WaitingStartKnown { get; set; }
    public DateTime OccurredAt { get; set; }
}
