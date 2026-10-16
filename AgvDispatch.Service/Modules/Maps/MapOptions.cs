using AgvDispatch.Domain.Maps;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class MapOptions
{
    public const string SectionName = "Map";

    public bool Enabled { get; set; }
    public int LeaseDurationSeconds { get; set; } = 120;
    public string ImportPath { get; set; } = string.Empty;
    public MapImportFormat ImportFormat { get; set; } = MapImportFormat.Auto;
    public double CoordinateScale { get; set; } = 1;
    public double CoordinateTolerance { get; set; } = 0.01;
    public string ValidationReportPath { get; set; } = string.Empty;
    public string UnderlayCachePath { get; set; } = "data/map-underlays";
    public string BackupPath { get; set; } = "data/map-backups";
    public int PositionStaleSeconds { get; set; } = 5;
    public double MinimumLocalizationConfidence { get; set; } = 0.5;
    public double MaximumRouteDeviationMeters { get; set; } = 0.75;
    public int OffRouteAlarmDelaySeconds { get; set; } = 10;
    public int TrackSamplingIntervalMs { get; set; } = 1000;
    public double TrackMinimumDistanceMeters { get; set; } = 0.05;
    public int TrackStationaryHeartbeatSeconds { get; set; } = 10;
    public int TrackRetentionDays { get; set; } = 30;
    public int TrafficMonitorIntervalMs { get; set; } = 1000;
    public int CongestionVehicleThreshold { get; set; } = 2;
    public double StationaryDistanceMeters { get; set; } = 0.05;
    public int CongestionStationarySeconds { get; set; } = 10;
    public int DeadlockDetectionSeconds { get; set; } = 30;
    public double OccupiedEdgePenalty { get; set; } = 5;
    public double CongestedEdgePenalty { get; set; } = 30;
    public double StalledEdgePenalty { get; set; } = 120;
    public List<MapNodeOptions> Nodes { get; set; } = [];
    public List<MapEdgeOptions> Edges { get; set; } = [];
    public List<TrafficResourceOptions> Resources { get; set; } = [];

    public string? GetConfigurationError()
    {
        if (!Enabled)
        {
            return null;
        }

        if (LeaseDurationSeconds is < 30 or > 86400)
        {
            return "Map:LeaseDurationSeconds必须在30到86400秒之间。";
        }

        if (!double.IsFinite(CoordinateScale) || CoordinateScale <= 0 ||
            !double.IsFinite(CoordinateTolerance) || CoordinateTolerance <= 0)
        {
            return "Map坐标缩放比例和校验容差必须是大于0的有限数值。";
        }

        if (PositionStaleSeconds is < 1 or > 300 ||
            !double.IsFinite(MinimumLocalizationConfidence) || MinimumLocalizationConfidence is < 0 or > 1 ||
            !double.IsFinite(MaximumRouteDeviationMeters) || MaximumRouteDeviationMeters <= 0 ||
            OffRouteAlarmDelaySeconds is < 1 or > 3600 ||
            TrackSamplingIntervalMs is < 200 or > 60000 ||
            !double.IsFinite(TrackMinimumDistanceMeters) || TrackMinimumDistanceMeters < 0 ||
            TrackStationaryHeartbeatSeconds is < 1 or > 3600 ||
            TrackRetentionDays is < 1 or > 3650 ||
            TrafficMonitorIntervalMs is < 200 or > 60000 ||
            CongestionVehicleThreshold is < 2 or > 100 ||
            !double.IsFinite(StationaryDistanceMeters) || StationaryDistanceMeters <= 0 ||
            CongestionStationarySeconds is < 1 or > 3600 ||
            DeadlockDetectionSeconds < CongestionStationarySeconds || DeadlockDetectionSeconds > 86400 ||
            !IsPenalty(OccupiedEdgePenalty) || !IsPenalty(CongestedEdgePenalty) ||
            !IsPenalty(StalledEdgePenalty) ||
            OccupiedEdgePenalty > CongestedEdgePenalty || CongestedEdgePenalty > StalledEdgePenalty)
        {
            return "地图实时定位阈值配置无效。";
        }

        if (!string.IsNullOrWhiteSpace(ImportPath))
        {
            return null;
        }

        if (Nodes.Count < 2 || Edges.Count == 0)
        {
            return "启用地图路由前至少需要两个节点和一条路段。";
        }

        try
        {
            _ = BuildGraph();
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    public MapDefinition BuildDefinition()
    {
        var nodes = Nodes.Select(node => new MapNode(
            node.Id,
            node.X,
            node.Y,
            node.Type,
            node.Enabled,
            node.ResourceIds)).ToArray();
        var nodesById = nodes.GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var edges = Edges.Select(options =>
        {
            var edge = new MapEdge(options.Id, options.From, options.To, options.Distance,
                options.SpeedLimit, options.Bidirectional, options.Enabled, options.ResourceIds)
            {
                GeometryKind = options.GeometryKind,
                ControlPoints = options.ControlPoints.Count == 0 ? null :
                    options.ControlPoints.Select(point => new MapPoint2D(point.X, point.Y)).ToArray()
            };
            return edge.GeometryKind != MapEdgeGeometryKind.Line &&
                   nodesById.TryGetValue(edge.From, out var from) && nodesById.TryGetValue(edge.To, out var to) &&
                   MapEdgeGeometry.TryValidate(edge, from, to, out _)
                ? edge with { Distance = MapEdgeGeometry.Length(edge, from, to) }
                : edge;
        }).ToArray();
        return new MapDefinition("InlineMap", "appsettings.json", "LocalCartesian", "m", nodes, edges,
            Resources.Select(resource => new TrafficResourceDefinition(
                resource.Id, resource.Type, resource.Capacity)).ToArray());
    }

    public MapGraph BuildGraph() => BuildDefinition().ToGraph();

    private static bool IsPenalty(double value) => double.IsFinite(value) && value >= 0;
}

public sealed class MapNodeOptions
{
    public string Id { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public MapNodeType Type { get; set; } = MapNodeType.Waypoint;
    public bool Enabled { get; set; } = true;
    public string[] ResourceIds { get; set; } = [];
}

public sealed class MapEdgeOptions
{
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public double Distance { get; set; }
    public double SpeedLimit { get; set; } = 1;
    public bool Bidirectional { get; set; }
    public bool Enabled { get; set; } = true;
    public string[] ResourceIds { get; set; } = [];
    public MapEdgeGeometryKind GeometryKind { get; set; } = MapEdgeGeometryKind.Line;
    public List<MapControlPointOptions> ControlPoints { get; set; } = [];
}

public sealed class MapControlPointOptions
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class TrafficResourceOptions
{
    public string Id { get; set; } = string.Empty;
    public TrafficResourceType Type { get; set; }
    public int Capacity { get; set; } = 1;
}
