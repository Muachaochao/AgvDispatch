using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("agv_track_point")]
public sealed class AgvTrackPointEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string VehicleId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string MapId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string MapVersionId { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? Angle { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? LocalizationConfidence { get; set; }

    public int PositionState { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? DistanceToRouteMeters { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? NearestEdgeId { get; set; }

    public DateTime SampledAt { get; set; }
}
