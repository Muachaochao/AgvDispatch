using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public sealed class ActiveMapRuntimeSnapshot
{
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");
    public string MapId { get; }
    public string VersionId { get; }
    public MapDefinition Definition { get; }
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
                  ControlPoints = edge.ControlPoints is null ? null : Array.AsReadOnly(edge.ControlPoints.ToArray()) }).ToArray()),
            Resources = Array.AsReadOnly(definition.Resources.Select(resource => resource with
                { Boundary = resource.Boundary is null ? null : Array.AsReadOnly(resource.Boundary.ToArray()) }).ToArray())
        };
        Graph = Definition.ToGraph();
    }
}

public interface IActiveMapRuntime
{
    ActiveMapRuntimeSnapshot? Current { get; }
    MapGraph? CurrentGraph { get; }
    void Replace(ActiveMapRuntimeSnapshot? snapshot);
}
