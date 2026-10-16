using System.Windows;
using System.Windows.Media;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Wpf.ViewModels;

public sealed record TrafficMapLine(string Id, PointCollection Points, string Stroke, double Thickness, string Hint)
{ public DoubleCollection Dash { get; init; } = []; }
public sealed record TrafficMapArea(string Id, PointCollection Points, string Stroke, string Fill, string Hint);
public sealed record TrafficMapDot(string Id, double Left, double Top, string Fill, string Hint, double Diameter = 12)
{ public string Label { get; init; } = Id; public double LabelTop { get; init; } = Diameter + 4; }
public sealed record TrafficMapDrawing(IReadOnlyList<TrafficMapLine> Edges, IReadOnlyList<TrafficMapLine> Routes,
    IReadOnlyList<TrafficMapArea> Areas, IReadOnlyList<TrafficMapDot> Nodes, IReadOnlyList<TrafficMapDot> Vehicles, string Notice)
{ public double CanvasHeight { get; init; } = 500; }

public static class TrafficMapOverlayBuilder
{
    public static TrafficMapDrawing Build(TrafficMapSnapshot? map, TrafficRequestSnapshot? request, DateTimeOffset? evaluatedAt = null)
    {
        var lines = new List<TrafficMapLine>(); var routes = new List<TrafficMapLine>(); var areas = new List<TrafficMapArea>();
        var nodes = new List<TrafficMapDot>(); var vehicles = new List<TrafficMapDot>(); var notes = new List<string>();
        var canvasHeight = 500d;
        TrafficMapDrawing Result(string notice) => new(lines,routes,areas,nodes,vehicles,notice) { CanvasHeight = canvasHeight };
        if (map?.Definition == null || map.MapId == null || map.VersionId == null) return Result("未取得运行地图，无法绘制关联。");
        var definition = map.Definition;
        var nodeById = definition.Nodes.Where(n => double.IsFinite(n.X) && double.IsFinite(n.Y)).GroupBy(n => n.Id).ToDictionary(g => g.Key,g => g.First());
        var sampled = new List<(MapEdge Edge,IReadOnlyList<MapPoint2D> Points)>();
        foreach (var edge in definition.Edges)
            if (nodeById.TryGetValue(edge.From,out var from) && nodeById.TryGetValue(edge.To,out var to) && MapEdgeGeometry.TryValidate(edge,from,to,out _))
                sampled.Add((edge,MapEdgeGeometry.Sample(edge,from,to,128)));
        var boundaries = definition.Resources.Where(r => r.Boundary is { Count: >=3 } && r.Boundary.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y))).ToArray();
        var world = nodeById.Values.Select(n => new MapPoint2D(n.X,n.Y)).Concat(sampled.SelectMany(s=>s.Points)).Concat(boundaries.SelectMany(r=>r.Boundary!)).ToArray();
        if (world.Length == 0) return Result("运行地图没有可绘制的导航几何。");
        var minX=world.Min(p=>p.X);var minY=world.Min(p=>p.Y);var width=Math.Max(world.Max(p=>p.X)-minX,1);var height=Math.Max(world.Max(p=>p.Y)-minY,1);
        canvasHeight = Math.Clamp(900 * height / width, 260, 600);
        var scale=Math.Min(820/width,(canvasHeight-80)/height);
        Point Project(MapPoint2D p)=>new((900-width*scale)/2+(p.X-minX)*scale,(canvasHeight+height*scale)/2-(p.Y-minY)*scale);
        PointCollection Points(IEnumerable<MapPoint2D> p) { var result=new PointCollection(p.Select(Project));result.Freeze();return result; }
        foreach(var (edge,points) in sampled) lines.Add(new(edge.Id,Points(points),"#7B8A9A",edge.Enabled?2:1,"导航路段 "+edge.Id));
        foreach(var n in nodeById.Values)
        { var p=Project(new(n.X,n.Y)); nodes.Add(new(n.Id,p.X-5,p.Y-5,"#8094A8","导航节点 "+n.Id,10)); }
        if(request == null) return Result("选择路权请求或占用查看地图关联。底图为运行拓扑，非车辆实测轨迹。");
        if(request.MapId == null || request.MapVersionId == null) return Result("所选请求未记录地图版本，保留文字关联，不按当前地图推算旧路线。");
        if(request.MapId!=map.MapId || request.MapVersionId!=map.VersionId) return Result($"请求地图 {request.MapId}/{request.MapVersionId} 与运行地图 {map.MapId}/{map.VersionId} 不同，已停止绘制请求高亮。");
        var now=evaluatedAt??DateTimeOffset.Now;
        if(map.CapturedAt > now.AddSeconds(1) || now-map.CapturedAt > TimeSpan.FromSeconds(Math.Max(map.PositionMaxAgeSeconds,5)))
            return Result("地图快照已过期，等待重新采集，已清除请求高亮及车辆位置。");
        var edgeIds=request.EdgeIds.ToHashSet(StringComparer.Ordinal);
        foreach(var (edge,points) in sampled.Where(s=>edgeIds.Contains(s.Edge.Id)))
            routes.Add(new(edge.Id,Points(points),"#2389F7",5,request.State=="Waiting"?"候选等待路径，未授予通行："+edge.Id:"任务路线："+edge.Id)
                { Dash=request.State=="Waiting"?new DoubleCollection([6,3]):[] });
        var missingEdges=edgeIds.Except(sampled.Select(s=>s.Edge.Id)).ToArray();
        if(missingEdges.Length>0) notes.Add("未找到路段："+string.Join(",",missingEdges));
        var validBlockers=request.Blockers.Where(b=>b.MapId==map.MapId && b.MapVersionId==map.VersionId).ToArray();
        var unknownBlockers=request.Blockers.Except(validBlockers).Select(b=>b.VehicleId).Distinct().ToArray();
        if(unknownBlockers.Length>0) notes.Add("阻塞方地图版本不匹配/未知："+string.Join(",",unknownBlockers));
        var held=request.HeldResources.Where(h=>h.MapId==map.MapId && h.MapVersionId==map.VersionId).Select(h=>h.ResourceId).ToHashSet();
        var unknownHeld=request.HeldResourceIds.Except(held).ToArray();
        if(unknownHeld.Length>0) notes.Add("持有资源地图版本不匹配/未知："+string.Join(",",unknownHeld));
        var blocked=validBlockers.Select(b=>b.ResourceId).ToHashSet();
        var wanted=request.ResourceIds.Concat(held).Concat(blocked).ToHashSet();
        foreach(var resource in definition.Resources.Where(r=>wanted.Contains(r.Id)))
        {
            if(unknownHeld.Contains(resource.Id) && !held.Contains(resource.Id) && !blocked.Contains(resource.Id)) continue;
            var color=blocked.Contains(resource.Id)?"#EF5350":held.Contains(resource.Id)?"#EBA12C":"#9A72DA";
            var tag=blocked.Contains(resource.Id)?"阻塞资源":held.Contains(resource.Id)?"当前持有资源":"申请资源（未持有）";
            var hint=tag+" "+resource.Id+(held.Contains(resource.Id)&&blocked.Contains(resource.Id)?"；本任务也持有部分容量":"");
            if(boundaries.Contains(resource)) areas.Add(new(resource.Id,Points(resource.Boundary!),color,"#22"+color[1..],hint));
            else
            {
                foreach(var (edge,points) in sampled.Where(s=>s.Edge.ResourceIds.Contains(resource.Id)))
                    routes.Insert(0,new(resource.Id+":"+edge.Id,Points(points),color,9,hint+"；按路段资源关联显示，不代表区域边界"));
                foreach(var n in nodeById.Values.Where(n=>n.ResourceIds.Contains(resource.Id)))
                {var p=Project(new(n.X,n.Y));nodes.Add(new(resource.Id+"@"+n.Id,p.X-10,p.Y-10,color,hint+"；节点关联，非区域边界",20) { Label = resource.Id, LabelTop = -24 });}
            }
        }
        var missingResources=wanted.Except(definition.Resources.Select(r=>r.Id)).ToArray();
        if(missingResources.Length>0) notes.Add("地图未配置资源："+string.Join(",",missingResources));
        var blockingVehicles=validBlockers.Select(b=>b.VehicleId).ToHashSet();
        foreach(var id in blockingVehicles.Append(request.VehicleId).Where(id=>!string.IsNullOrWhiteSpace(id)).Distinct())
        {
            var v=map.Vehicles.FirstOrDefault(v=>v.VehicleId==id);
            if(v==null || v.Connection!=ConnectionState.Online || v.Quality!=SignalQuality.Good || v.LocalizationConfidence==null || v.LastSuccessfulPollAt==null || v.LastSuccessfulPollAt>now.AddSeconds(1))
            { notes.Add(id+"：位置缺失、离线或质量不合格"); continue; }
            var position=AgvMapPositionAnalyzer.Analyze(v.X,v.Y,v.LocalizationConfidence,v.LastSuccessfulPollAt,definition,now,
                TimeSpan.FromSeconds(map.PositionMaxAgeSeconds),map.MinimumLocalizationConfidence,map.MaximumRouteDeviationMeters);
            if(position.State!=AgvMapPositionState.Normal) { notes.Add(id+"："+position.Message); continue; }
            var p=Project(new(v.X!.Value,v.Y!.Value));var blocking=blockingVehicles.Contains(id);
            vehicles.Add(new(id,p.X-11,p.Y-11,blocking?"#EF5350":"#2389F7",(blocking?"阻塞车辆":"申请车辆")+" "+id+" · 新鲜定位观测",22));
        }
        return Result($"地图 {map.MapId}/{map.VersionId} · 蓝色路线{(request.State=="Waiting"?"为候选等待路径":"为规划路径")}；资源为逻辑路权，车辆为定位观测。"+(notes.Count==0?"":" "+string.Join("；",notes)));
    }
}
