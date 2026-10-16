using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Application.Traffic;

// Pure spatial preview. No temporal scheduling, database, CAD importer or vehicle command dependency.
public static class TrafficGeometryPreviewEvaluator
{
    private const double Epsilon=1e-6;
    public static TrafficGeometryPreview Evaluate(MapDefinition map,string mapId,string versionId,
        TrafficGeometryRoute first,TrafficGeometryRoute second,TrafficGeometryPreviewPolicy policy,CancellationToken ct=default)
    {
        TrafficGeometryPreview Refuse(string message)=>new(false,false,message,0,[],[],[]);
        if(policy is null)return Refuse("缺少预检参数。");
        if(!policy.Enabled)return Refuse("几何冲突预检未启用。");
        if(!double.IsFinite(policy.SafetyMarginMeters)||policy.SafetyMarginMeters is <.1 or >10||
            policy.MaximumEdgesPerRoute is <1 or >10000||policy.MaximumComparisons is <1 or >1000000||
            policy.MaximumReportedConflicts is <1 or >1000||policy.MaximumIndexVisits is <1 or >2000000)return Refuse("预检参数无效。");
        if(map is null||map.Nodes is null||map.Edges is null||map.Nodes.Count>100000||map.Edges.Count>100000||
            map.Unit!="m"||string.IsNullOrWhiteSpace(mapId)||string.IsNullOrWhiteSpace(versionId)||string.IsNullOrWhiteSpace(map.CoordinateSystem))
            return Refuse("须提供有版本的米制导航地图，且地图规模不超预算。");
        try
        {
            ct.ThrowIfCancellationRequested();
            var nodes=new Dictionary<string,MapNode>(StringComparer.OrdinalIgnoreCase);
            var edges=new Dictionary<string,MapEdge>(StringComparer.OrdinalIgnoreCase);
            foreach(var node in map.Nodes)
                if(node is null||string.IsNullOrWhiteSpace(node.Id)||!nodes.TryAdd(node.Id,node))throw new ArgumentException("地图节点编号无效或重复。");
            foreach(var edge in map.Edges)
                if(edge is null||string.IsNullOrWhiteSpace(edge.Id)||!edges.TryAdd(edge.Id,edge))throw new ArgumentException("导航路段编号无效或重复。");
            var a=Build(first);var b=Build(second);
            if(first.VehicleId==second.VehicleId)throw new ArgumentException("同一车辆的两条路线不能用作两车冲突预检。");
            var conflicts=new List<TrafficGeometryConflict>();var comparisons=0;var visits=0;
            var firstAreas=a.Select(x=>x.Area).ToArray();var secondAreas=b.Select(x=>x.Area).ToArray();
            var index=policy.UseSpatialIndex?BuildIndex(Enumerable.Range(0,b.Count).ToArray()):null;
            foreach(var x in a)
            {
                var candidates=new List<int>();
                if(index is not null)
                {
                    if(!Find(index,x.Area.Bounds,candidates))return Result(false,"空间索引遍历预算耗尽，不能判定路线分离。");
                    candidates.Sort(); // Preserve original route order for deterministic reports and limits.
                }
                else candidates.AddRange(Enumerable.Range(0,b.Count));
                foreach(var candidate in candidates)
                {
                var y=b[candidate];
                ct.ThrowIfCancellationRequested();
                if(comparisons>=policy.MaximumComparisons)return Result(false,"比较预算耗尽，不能判定路线分离。");
                comparisons++;
                if(!Overlap(x.Area.Bounds,y.Area.Bounds))continue;
                var bothLines=x.Area.GeometryKind==MapEdgeGeometryKind.Line&&y.Area.GeometryKind==MapEdgeGeometryKind.Line;
                if(bothLines&&SegmentDistance(x.From,x.To,y.From,y.To)>x.Area.EnvelopeRadius+y.Area.EnvelopeRadius+Epsilon)continue;
                conflicts.Add(new(x.Area.EdgeId,y.Area.EdgeId,bothLines?TrafficGeometryConflictKind.LineEnvelopeOverlap:TrafficGeometryConflictKind.ConservativeCurveOverlap,
                    bothLines?"直线扫掠车体包络交叠或接触，须检查共享区域。":"曲线保守包络交叠，可能包含实际曲线外区域，须进一步核对。"));
                if(conflicts.Count>=policy.MaximumReportedConflicts)return Result(false,"冲突报告数量达到上限，尚未遍历全部路线。");
                }
            }
            return Result(true,conflicts.Count==0?"已在当前空间模型下完整检查且未发现交叠；未考虑时间与实时车辆状态。":"发现空间交叠候选；不自动加锁或解锁。");

            TrafficGeometryPreview Result(bool complete,string reason)=>new(true,complete,reason,comparisons,firstAreas,secondAreas,conflicts){IndexVisits=visits};
            BoundsNode BuildIndex(int[] ids)
            {
                ct.ThrowIfCancellationRequested();
                var bounds=new MapGeometryBounds(ids.Min(i=>b[i].Area.Bounds.MinX),ids.Min(i=>b[i].Area.Bounds.MinY),
                    ids.Max(i=>b[i].Area.Bounds.MaxX),ids.Max(i=>b[i].Area.Bounds.MaxY));
                if(ids.Length==1)return new(bounds,ids[0],null,null);
                var horizontal=bounds.MaxX-bounds.MinX>=bounds.MaxY-bounds.MinY;
                Array.Sort(ids,(i,j)=>
                {
                    var p=b[i].Area.Bounds;var q=b[j].Area.Bounds;
                    var order=(horizontal?p.MinX+p.MaxX:p.MinY+p.MaxY).CompareTo(horizontal?q.MinX+q.MaxX:q.MinY+q.MaxY);
                    return order!=0?order:i.CompareTo(j);
                });
                var middle=ids.Length/2;
                return new(bounds,-1,BuildIndex(ids[..middle]),BuildIndex(ids[middle..]));
            }
            bool Find(BoundsNode node,MapGeometryBounds query,List<int> found)
            {
                ct.ThrowIfCancellationRequested();
                if(visits>=policy.MaximumIndexVisits)return false;
                visits++;
                if(!Overlap(query,node.Bounds))return true;
                if(node.Index>=0){found.Add(node.Index);return true;}
                return Find(node.Left!,query,found)&&Find(node.Right!,query,found);
            }

            List<Section> Build(TrafficGeometryRoute route)
            {
                if(route is null||new[]{route.TaskId,route.StepId,route.VehicleId}.Any(string.IsNullOrWhiteSpace)||
                    route.MapId!=mapId||route.MapVersionId!=versionId||route.CoordinateFrameId!=map.CoordinateSystem)
                    throw new ArgumentException("路线身份、地图版本或坐标系不一致。");
                var body=route.Body;
                if(body is null||!body.CalibrationVerified||!body.IncludesMaximumPermittedLoad||string.IsNullOrWhiteSpace(body.ProfileId)||
                    body.VehicleId!=route.VehicleId||body.CoordinateFrameId!=route.CoordinateFrameId||
                    !double.IsFinite(body.RadiusFromReportedReferenceMeters)||body.RadiusFromReportedReferenceMeters is <=0 or >100||
                    !double.IsFinite(route.PositionUncertaintyMeters)||route.PositionUncertaintyMeters is <0 or >.25)
                    throw new ArgumentException("缺少核定整车最大载荷包络或有效定位误差界。");
                if(route.EdgeIds is null||route.NodeIds is null||route.EdgeIds.Count<1||route.EdgeIds.Count>policy.MaximumEdgesPerRoute||
                    route.NodeIds.Count!=route.EdgeIds.Count+1)throw new ArgumentException("须提供预算内完整有序导航路线。");
                if(route.NodeIds.Any(string.IsNullOrWhiteSpace)||route.EdgeIds.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("路线节点或路段编号无效。");
                var radius=body.RadiusFromReportedReferenceMeters+route.PositionUncertaintyMeters+policy.SafetyMarginMeters;
                var result=new List<Section>();
                for(var i=0;i<route.EdgeIds.Count;i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if(!nodes.TryGetValue(route.NodeIds[i],out var from)||!nodes.TryGetValue(route.NodeIds[i+1],out var to)||
                        !edges.TryGetValue(route.EdgeIds[i],out var edge)||!from.Enabled||!to.Enabled||!edge.Enabled||
                        !(Eq(edge.From,from.Id)&&Eq(edge.To,to.Id)||edge.Bidirectional&&Eq(edge.To,from.Id)&&Eq(edge.From,to.Id)))
                        throw new ArgumentException("路线断链、禁用或方向不正确；普通绘图对象不能代替导航路段。");
                    if(new[]{from.X,from.Y,to.X,to.Y}.Any(v=>!ValidCoordinate(v))||edge.ControlPoints?.Any(p=>p is null||!ValidCoordinate(p.X)||!ValidCoordinate(p.Y))==true||
                        !MapEdgeGeometry.TryValidate(edge,nodes[edge.From],nodes[edge.To],out _))throw new ArgumentException("路线几何不可用。");
                    var box=MapEdgeGeometry.ConservativeBounds(edge,nodes[edge.From],nodes[edge.To]);
                    if(new[]{box.MinX,box.MinY,box.MaxX,box.MaxY}.Any(v=>!ValidCoordinate(v)))throw new ArgumentException("曲线保守边界超过计算范围。");
                    var expanded=new MapGeometryBounds(box.MinX-radius,box.MinY-radius,box.MaxX+radius,box.MaxY+radius);
                    result.Add(new(new(edge.Id,edge.GeometryKind,expanded,radius),new(from.X,from.Y),new(to.X,to.Y)));
                }
                return result;
            }
        }
        catch(ArgumentException ex){return Refuse(ex.Message);}
        catch(KeyNotFoundException){return Refuse("导航路段端点不存在。");}
        catch(InvalidOperationException){return Refuse("几何计算无法稳定完成。");}
    }
    private static bool Eq(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    private static bool ValidCoordinate(double value)=>double.IsFinite(value)&&Math.Abs(value)<=1e6;
    private static bool Overlap(MapGeometryBounds a,MapGeometryBounds b)=>a.MinX<=b.MaxX+Epsilon&&a.MaxX+Epsilon>=b.MinX&&a.MinY<=b.MaxY+Epsilon&&a.MaxY+Epsilon>=b.MinY;
    private static double SegmentDistance(MapPoint2D a,MapPoint2D b,MapPoint2D c,MapPoint2D d)
    {
        double Cross(MapPoint2D p,MapPoint2D q,MapPoint2D r)=>(q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        var abC=Cross(a,b,c);var abD=Cross(a,b,d);var cdA=Cross(c,d,a);var cdB=Cross(c,d,b);
        if((abC>0&&abD<0||abC<0&&abD>0)&&(cdA>0&&cdB<0||cdA<0&&cdB>0))return 0;
        return new[]{PointDistance(a,c,d),PointDistance(b,c,d),PointDistance(c,a,b),PointDistance(d,a,b)}.Min();
    }
    private static double PointDistance(MapPoint2D p,MapPoint2D a,MapPoint2D b)
    {
        var dx=b.X-a.X;var dy=b.Y-a.Y;var denominator=dx*dx+dy*dy;
        var t=denominator==0?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/denominator,0,1);
        var x=p.X-(a.X+t*dx);var y=p.Y-(a.Y+t*dy);return Math.Sqrt(x*x+y*y);
    }
    private sealed record Section(TrafficSweptArea Area,MapPoint2D From,MapPoint2D To);
    private sealed record BoundsNode(MapGeometryBounds Bounds,int Index,BoundsNode? Left,BoundsNode? Right);
}
