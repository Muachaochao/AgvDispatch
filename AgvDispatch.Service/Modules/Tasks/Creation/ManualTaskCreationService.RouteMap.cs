using System.IO;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;
public sealed partial class ManualTaskCreationService
{
    private static ManualTaskRouteMap BuildTaskRouteMap(ActiveMapRuntimeSnapshot source,string observationId,IReadOnlyList<ManualTaskRouteSegment> segments,CancellationToken token)
    {
        var map=source.Definition;if(map.Unit!="m")throw new InvalidDataException("仅显示明确米制导航图。");
        var nodes=map.Nodes.ToDictionary(n=>n.Id,StringComparer.Ordinal);var edges=map.Edges.ToDictionary(e=>e.Id,StringComparer.Ordinal);
        var required=segments.Select(s=>s.EdgeId).Distinct(StringComparer.Ordinal).ToArray();
        var candidates=required.Where(edges.ContainsKey).Select(id=>edges[id]).Concat(map.Edges.Where(e=>e.Enabled)).DistinctBy(e=>e.Id).Take(1000).ToArray();
        static bool Coordinate(double v)=>double.IsFinite(v)&&Math.Abs(v)<=1e6;
        static bool Id(string? v)=>!string.IsNullOrWhiteSpace(v)&&v.Length<=256;
        var output=new List<ManualTaskRouteMapEdge>();var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var edge in candidates)
        {
            token.ThrowIfCancellationRequested();
            if(!Id(edge.Id)||!nodes.TryGetValue(edge.From,out var from)||!nodes.TryGetValue(edge.To,out var to)||!Id(from.Id)||!Id(to.Id)||!Coordinate(from.X)||!Coordinate(from.Y)||!Coordinate(to.X)||!Coordinate(to.Y)||!MapEdgeGeometry.TryValidate(edge,from,to,out _))continue;
            if((from.AgvSiteId?.Length??0)>256||(to.AgvSiteId?.Length??0)>256)continue;
            var points=MapEdgeGeometry.Sample(edge,from,to,32);
            if(points.Count is <2 or >33||points.Any(p=>!Coordinate(p.X)||!Coordinate(p.Y)))continue;
            output.Add(new(edge.Id,edge.From,edge.To,edge.Bidirectional,edge.GeometryKind.ToString(),points.ToArray()));ids.Add(edge.From);ids.Add(edge.To);
        }
        foreach(var node in map.Nodes.Where(n=>n.Enabled))
        {
            token.ThrowIfCancellationRequested();if(ids.Count>=2048)break;
            if(Id(node.Id)&&(node.AgvSiteId?.Length??0)<=256&&Coordinate(node.X)&&Coordinate(node.Y))ids.Add(node.Id);
        }
        var rows=ids.Select(id=>nodes[id]).Select(n=>new ManualTaskRouteMapNode(n.Id,n.AgvSiteId??"",n.X,n.Y)).ToArray();
        return new(source.MapId,source.VersionId,observationId,"m",map.Nodes.Count,map.Edges.Count,rows,output){RuntimeId=source.RuntimeId};
    }
}
