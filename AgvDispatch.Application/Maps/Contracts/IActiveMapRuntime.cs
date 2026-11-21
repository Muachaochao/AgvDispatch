using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public sealed class ActiveMapRuntimeSnapshot
{
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");
    public string MapId { get; }
    public string VersionId { get; }
    public MapDefinition Definition { get; }
    public string DefinitionHash { get; }
    public DateTimeOffset ActivatedAt { get; }
    public MapGraph Graph { get; }

    public ActiveMapRuntimeSnapshot(string mapId, string versionId, MapDefinition definition, DateTimeOffset activatedAt)
    {
        MapId = mapId;
        VersionId = versionId;
        ActivatedAt = activatedAt;
        Definition = definition with
        {
            Nodes = Array.AsReadOnly(definition.Nodes.Select(node => node with
                { ResourceIds = Array.AsReadOnly(node.ResourceIds.ToArray()) }).ToArray()),
            Edges = Array.AsReadOnly(definition.Edges.Select(edge => edge with
                { ResourceIds = Array.AsReadOnly(edge.ResourceIds.ToArray()),
                  ControlPoints = Freeze(edge.ControlPoints),
                  VendorPath = edge.VendorPath is { } path ? path with { ControlPoints = Freeze(path.ControlPoints) } : null }).ToArray()),
            Resources = Array.AsReadOnly(definition.Resources.Select(resource => resource with
                { Boundary = Freeze(resource.Boundary) }).ToArray()),
            AreaAnnotations = definition.AreaAnnotations is { } annotations
                ? Array.AsReadOnly(annotations.Select(area => area with { Boundary = Freeze(area.Boundary)! }).ToArray()) : null,
            ImportWarnings = Array.AsReadOnly(definition.ImportWarnings.ToArray()),
            Layers = Array.AsReadOnly(definition.Layers.ToArray())
        };
        DefinitionHash = EngineeringRuntimeIdentity.MapHash(Definition);
        Graph = Definition.ToGraph();
    }

    private static IReadOnlyList<MapPoint2D>? Freeze(IReadOnlyList<MapPoint2D>? points) =>
        points is null ? null : Array.AsReadOnly(points.ToArray());
}

public interface IActiveMapRuntime
{
    ActiveMapRuntimeSnapshot? Current { get; }
    MapGraph? CurrentGraph { get; }
    void Replace(ActiveMapRuntimeSnapshot? snapshot);
}
