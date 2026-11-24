namespace AgvDispatch.Domain.Maps;

public enum MapNodeType
{
    Waypoint = 0,
    Station = 10,
    Intersection = 20,
    WaitingPoint = 30,
    ChargingPoint = 40
}

public enum TrafficResourceType
{
    Intersection = 0,
    NarrowAisle = 10,
    SingleLane = 20,
    Station = 30,
    ChargingBay = 40
}

public sealed record MapNode(
    string Id,
    double X,
    double Y,
    MapNodeType Type,
    bool Enabled,
    IReadOnlyList<string> ResourceIds,
    string? AgvSiteId = null)
{
    public string LayerId { get; init; } = "navigation";
    // A standby/stop role may also be a mandatory transit point on a corridor.
    // Traffic resources still block the node when that resource is unavailable.
    public bool AllowTransitThrough { get; init; }
    // A graph intersection is not necessarily a physical waiting bay. An
    // explicit veto prevents rolling navigation from stopping in a shared
    // corridor. Null preserves existing, already commissioned map behaviour.
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowTrafficStop { get; init; }
    // False excludes this node from dumper staging without affecting route windows.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowDumperWaiting { get; init; }
}

public enum MapEdgeGeometryKind
{
    Line = 0,
    CircularArc = 10,
    CubicBezier = 20
}

public sealed record MapEdge(
    string Id,
    string From,
    string To,
    double Distance,
    double SpeedLimit,
    bool Bidirectional,
    bool Enabled,
    IReadOnlyList<string> ResourceIds)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RobokitPathEvidence? VendorPath { get; init; }
    public string LayerId { get; init; } = "navigation";
    public MapEdgeGeometryKind GeometryKind { get; init; } = MapEdgeGeometryKind.Line;
    // CircularArc uses one point on the arc; CubicBezier uses two ordered control points.
    public IReadOnlyList<MapPoint2D>? ControlPoints { get; init; }
}

public sealed record MapPoint2D(double X, double Y);

public sealed record TrafficResourceDefinition(
    string Id,
    TrafficResourceType Type,
    int Capacity = 1,
    IReadOnlyList<MapPoint2D>? Boundary = null)
{
    public string LayerId { get; init; } = "navigation";
}

public sealed record RoutePlan(
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<string> ResourceIds,
    double Cost);
