using System.IO;
using System.Text.Json;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Wpf.Services;

public sealed record MapReviewRoadDetail(string Id,string From,string To,bool Bidirectional,IReadOnlyList<MapPoint2D> Points)
{
    public MapEdgeGeometryKind GeometryKind {get;init;}=MapEdgeGeometryKind.Line;
    public IReadOnlyList<MapPoint2D> ControlPoints {get;init;}=[];
}

public sealed record MapReviewSnapshot(string Name,string Sha256,IReadOnlyList<MapNode> Nodes,IReadOnlyList<IReadOnlyList<MapPoint2D>> Roads,int TotalRoads,int SkippedRoads)
{
    public IReadOnlyList<MapReviewRoadDetail> RoadDetails {get;init;}=[];
    public static MapReviewSnapshot Read(JsonElement root,string sha256,CancellationToken token)
    {
        if(root.TryGetProperty("draft",out var draft))root=draft.GetProperty("definition");
        if(root.GetProperty("unit").GetString()!="m")throw new InvalidDataException("只定位显式米制业务图，不猜坐标换算。");
        var nodeRows=root.GetProperty("nodes");var edgeRows=root.GetProperty("edges");
        if(nodeRows.ValueKind!=JsonValueKind.Array||nodeRows.GetArrayLength()>10000||edgeRows.ValueKind!=JsonValueKind.Array||edgeRows.GetArrayLength()>20000)throw new InvalidDataException("节点/道路数量超限或格式无效。");
        static string Text(JsonElement row,string field)
        {
            if(!row.TryGetProperty(field,out var v)||v.ValueKind!=JsonValueKind.String||v.GetString()!.Length>256||string.IsNullOrWhiteSpace(v.GetString()))throw new InvalidDataException("快照编号缺失或过长："+field);
            return v.GetString()!;
        }
        static double Number(JsonElement row,string field)
        {
            if(!row.TryGetProperty(field,out var v)||v.ValueKind!=JsonValueKind.Number||!v.TryGetDouble(out var n)||!double.IsFinite(n)||Math.Abs(n)>1e6)throw new InvalidDataException("快照坐标缺失或无效。");return n;
        }
        var nodes=new List<MapNode>();var ids=new Dictionary<string,MapNode>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in nodeRows.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();var id=Text(row,"id");
            string? vendor=null;if(row.TryGetProperty("agvSiteId",out var site)&&site.ValueKind!=JsonValueKind.Null){if(site.ValueKind!=JsonValueKind.String||site.GetString()!.Length>256)throw new InvalidDataException("厂家编号格式无效。");vendor=site.GetString();}
            var node=new MapNode(id,Number(row,"x"),Number(row,"y"),MapNodeType.Waypoint,false,[],vendor);
            if(!ids.TryAdd(id,node))throw new InvalidDataException("快照业务编号重复（含大小写冲突）。");nodes.Add(node);
        }
        var roads=new List<IReadOnlyList<MapPoint2D>>();var skipped=0;var details=new List<MapReviewRoadDetail>();
        foreach(var row in edgeRows.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var roadId=Text(row,"id");var bidirectional=false;
                if(row.TryGetProperty("bidirectional",out var direction)){if(direction.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("道路方向无效。");bidirectional=direction.GetBoolean();}
                var a=Text(row,"from");var b=Text(row,"to");
                if(!ids.TryGetValue(a,out var from)||from.Id!=a||!ids.TryGetValue(b,out var to)||to.Id!=b)throw new InvalidDataException("道路端点缺失。");
                var kind=MapEdgeGeometryKind.Line;
                if(row.TryGetProperty("geometryKind",out var geometry))
                {
                    if(geometry.ValueKind==JsonValueKind.String){if(!Enum.TryParse(geometry.GetString(),false,out kind))throw new InvalidDataException("未知几何。");}
                    else if(geometry.TryGetInt32(out var integer))kind=(MapEdgeGeometryKind)integer;else throw new InvalidDataException("几何类型无效。");
                }
                var controls=new List<MapPoint2D>();
                if(row.TryGetProperty("controlPoints",out var points)&&points.ValueKind!=JsonValueKind.Null)
                {if(points.ValueKind!=JsonValueKind.Array||points.GetArrayLength()>2)throw new InvalidDataException("控制点无效。");foreach(var p in points.EnumerateArray())controls.Add(new(Number(p,"x"),Number(p,"y")));}
                var edge=new MapEdge("preview",a,b,1,1,false,false,[]){GeometryKind=kind,ControlPoints=controls};
                if(!MapEdgeGeometry.TryValidate(edge,from,to,out _))throw new InvalidDataException("几何无效。");
                var sampled=MapEdgeGeometry.Sample(edge,from,to,32);
                if(sampled.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||Math.Abs(p.X)>1e6||Math.Abs(p.Y)>1e6))throw new InvalidDataException("几何范围超限。");
                details.Add(new(roadId,a,b,bidirectional,sampled){GeometryKind=kind,ControlPoints=controls.ToArray()});
                if(roads.Count<3000)roads.Add(sampled);else skipped++;
            }
            catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException or ArgumentException){skipped++;}
        }
        var name=root.TryGetProperty("name",out var label)&&label.ValueKind==JsonValueKind.String?label.GetString()!:"未命名业务图";
        return new(name.Length>256?name[..256]:name,sha256,nodes,roads,edgeRows.GetArrayLength(),skipped){RoadDetails=details};
    }
}
