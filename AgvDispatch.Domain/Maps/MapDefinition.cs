namespace AgvDispatch.Domain.Maps;

public sealed record MapDefinition(
    string Name,
    string Source,
    string CoordinateSystem,
    string Unit,
    IReadOnlyList<MapNode> Nodes,
    IReadOnlyList<MapEdge> Edges,
    IReadOnlyList<TrafficResourceDefinition> Resources)
{
    public IReadOnlyList<string> ImportWarnings { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RobokitMapSource? RobokitSource { get; init; }
    public IReadOnlyList<MapEditorLayer> Layers { get; init; } = [];
    public MapEditorSettings? EditorSettings { get; init; }
    public MapGraph ToGraph() => new(Nodes, Edges, Resources);
}

public sealed record MapEditorLayer(string Id, string Name, bool Visible = true)
{
    public bool IsLocked { get; init; }
    public bool IsAllowSelect { get; init; } = true;
    public double Alpha { get; init; } = 1;
    public string? Color { get; init; }
    public double StrokeWidth { get; init; } = 2;
}
public sealed record MapEditorSettings(double WorldWidth, double WorldHeight, double GridSize, bool ShowGrid, bool SnapToGrid, bool SnapToExisting)
{
    public MapDisplaySettings? Display { get; init; }
    public string? CurrentLayerId { get; init; }
    public bool SnapToAlignment { get; init; } = true;
    public bool SnapToGeometry { get; init; } = true;
}

public enum MapValidationSeverity
{
    Information = 0,
    Warning = 10,
    Error = 20
}

public sealed record MapValidationIssue(
    MapValidationSeverity Severity,
    string Code,
    string Message,
    IReadOnlyList<string> ObjectIds);

public sealed record MapValidationReport(
    IReadOnlyList<MapValidationIssue> Issues,
    int NodeCount,
    int EdgeCount,
    int ResourceCount)
{
    public bool IsValid => Issues.All(issue => issue.Severity != MapValidationSeverity.Error);
    public int ErrorCount => Issues.Count(issue => issue.Severity == MapValidationSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == MapValidationSeverity.Warning);
}

public sealed record MapDisplaySettings
{
    public bool Nodes {get;init;}=true;
    public bool Edges {get;init;}=true;
    public bool Resources {get;init;}=true;
    public bool Vehicles {get;init;}=true;
    public bool Underlay {get;init;}=true;
    public bool Scan {get;init;}=true;
    public bool Annotations {get;init;}=true;
    public double UnderlayOpacity {get;init;}=1;
    public bool Labels {get;init;}=true;
    public bool SparseLabels {get;init;}=true;
    public bool VendorLabels {get;init;}=true;
    public bool Directions {get;init;}=true;
    public bool OnlyCurrentLayer {get;init;}
}
