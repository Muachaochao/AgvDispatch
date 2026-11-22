using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Application.Traffic;

/// <summary>Read-only evidence assessment. It has no database, gateway or release capability.</summary>
public static class TrafficSegmentReleaseEvaluator
{
    public static TrafficSegmentReleaseAssessment Evaluate(TrafficSegmentReleaseContext context,TrafficProgressPair? evidence,
        TrafficSegmentReleasePolicy policy,DateTimeOffset now)
    {
        TrafficSegmentReleaseAssessment Reject(string reason)=>new(false,reason,[]);
        if(!policy.Enabled)return Reject("逐段释放评估未启用，保留全部路权。");
        if(!double.IsFinite(policy.MinimumObservationIntervalSeconds)||policy.MinimumObservationIntervalSeconds<0.1||policy.MinimumObservationIntervalSeconds>policy.MaximumAgeSeconds ||
            !double.IsFinite(policy.MaximumAgeSeconds)||policy.MaximumAgeSeconds<=0||policy.MaximumAgeSeconds>30 ||
            !double.IsFinite(policy.MinimumConfidence)||policy.MinimumConfidence<=0||policy.MinimumConfidence>1 ||
            !double.IsFinite(policy.MaximumUncertaintyMeters)||policy.MaximumUncertaintyMeters<0||policy.MaximumUncertaintyMeters>1 ||
            !double.IsFinite(policy.MaximumRouteDeviationMeters)||policy.MaximumRouteDeviationMeters<0.1||policy.MaximumRouteDeviationMeters>2 ||
            !double.IsFinite(policy.SafetyMarginMeters)||policy.SafetyMarginMeters<0.1||policy.SafetyMarginMeters>10)return Reject("评估阈值无效。");
        if(!context.TaskAndStepExecuting||!context.RecoveryAndMapGateOpen)return Reject("任务/步骤非执行中或恢复/地图门禁未开放。");
        if(!context.EvidenceSourceRegistered)return Reject("可信进度采集源未注册，不能从站点或单次定位推断。");
        if(new[]{context.TaskId,context.StepId,context.VehicleId,context.VendorTaskId,context.CommandId,context.RouteRevision,context.MapId,context.MapVersionId,context.CoordinateFrameId}.Any(string.IsNullOrWhiteSpace))return Reject("原运输与地图归属不完整。");
        if(context.Map.Unit!="m"||evidence==null)return Reject("缺少米制地图或两帧可信进度。");
        var body=context.Body;
        if(body==null||string.IsNullOrWhiteSpace(body.ProfileId)||body.VehicleId!=context.VehicleId||body.CoordinateFrameId!=context.CoordinateFrameId ||
            !body.CalibrationVerified||!body.IncludesMaximumPermittedLoad||!double.IsFinite(body.RadiusFromReportedReferenceMeters)||body.RadiusFromReportedReferenceMeters<=0||body.RadiusFromReportedReferenceMeters>100)
            return Reject("车辆报告参考点、完整车体和最大载荷包络未核定。");
        var a=evidence.First;var b=evidence.Second;
        if(a==null||b==null)return Reject("两帧进度不完整。");
        foreach(var f in new[]{a,b})
        {
            if(f.TaskId!=context.TaskId||f.StepId!=context.StepId||f.VehicleId!=context.VehicleId||f.VendorTaskId!=context.VendorTaskId||f.CommandId!=context.CommandId ||
                f.RouteRevision!=context.RouteRevision||f.MapId!=context.MapId||f.MapVersionId!=context.MapVersionId||f.CoordinateFrameId!=context.CoordinateFrameId)
                return Reject("进度与原车、原命令、路线或地图版本不一致。");
            if(!f.Online||!f.Coherent||f.Quality!=SignalQuality.Good||f.ObservedAt<context.SendIntentAt||f.ObservedAt>now||now-f.ObservedAt>TimeSpan.FromSeconds(policy.MaximumAgeSeconds))
                return Reject("进度断连、不一致、陈旧、未来或早于本次发送意图。");
            if(!Finite(f.X)||!Finite(f.Y)||!double.IsFinite(f.LocalizationConfidence)||f.LocalizationConfidence<policy.MinimumConfidence||f.LocalizationConfidence>1 ||
                !double.IsFinite(f.PositionUncertaintyMeters)||f.PositionUncertaintyMeters<0||f.PositionUncertaintyMeters>policy.MaximumUncertaintyMeters)
                return Reject("定位置信度、误差界或坐标不可用。");
            if(string.IsNullOrWhiteSpace(f.ControllerEpoch)||f.Sequence<0||f.LastFullyTraversedEdgeIndex< -1||f.LastFullyTraversedEdgeIndex>=context.RouteEdgeIds.Count)
                return Reject("控制器周期或经过进度无效。");
        }
        if(a.ControllerEpoch!=b.ControllerEpoch||b.Sequence<=a.Sequence||(b.ObservedAt-a.ObservedAt).TotalSeconds<policy.MinimumObservationIntervalSeconds||b.LastFullyTraversedEdgeIndex<a.LastFullyTraversedEdgeIndex)
            return Reject("控制器复位、样本重复、时间或路径进度倒退。");
        if(context.Leases.Any(l=>l==null)||context.Leases.Select(l=>l.LeaseId).Distinct().Count()!=context.Leases.Count)return Reject("租约列表重复或无效。");
        if(context.RouteEdgeIds.Count==0||context.RouteEdgeIds.Count>10000||context.RouteNodeIds.Count!=context.RouteEdgeIds.Count+1)
            return Reject("原路线节点与路段序列不完整。");
        if(context.Map.Nodes.Any(n=>n==null)||context.Map.Edges.Any(e=>e==null)||context.Map.Resources.Any(r=>r==null)||
            context.Map.Nodes.Select(n=>n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=context.Map.Nodes.Count||context.Map.Edges.Select(e=>e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=context.Map.Edges.Count||context.Map.Resources.Select(r=>r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=context.Map.Resources.Count)
            return Reject("地图对象重复或无效。");
        MapCorridorTopology topology;
        try { topology=MapCorridorResources.Enrich(context.Map); }
        catch(Exception exception) when(exception is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {return Reject("运行地图无法建立自动走廊资源，保持路权。");}
        var nodes=context.Map.Nodes.ToDictionary(n=>n.Id,StringComparer.OrdinalIgnoreCase);
        var edges=topology.Edges.ToDictionary(e=>e.Id,StringComparer.OrdinalIgnoreCase);
        var resources=topology.Resources.ToDictionary(r=>r.Id,StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<context.RouteEdgeIds.Count;i++)
        {
            if(!nodes.TryGetValue(context.RouteNodeIds[i],out var from)||!nodes.TryGetValue(context.RouteNodeIds[i+1],out var to)||!from.Enabled||!to.Enabled ||
                !edges.TryGetValue(context.RouteEdgeIds[i],out var edge)||!edge.Enabled ||
                !(edge.From==from.Id&&edge.To==to.Id||edge.Bidirectional&&edge.From==to.Id&&edge.To==from.Id) || !MapEdgeGeometry.TryValidate(edge,from,to,out _))return Reject("原路径存在断链、禁用或方向不符。");
        }
        foreach(var f in new[]{a,b})
        {
            var next=f.LastFullyTraversedEdgeIndex+1;
            double deviation;
            if(next>=context.RouteEdgeIds.Count)
            {
                var target=nodes[context.RouteNodeIds[^1]];deviation=Math.Sqrt((f.X-target.X)*(f.X-target.X)+(f.Y-target.Y)*(f.Y-target.Y));
            }
            else deviation=MapEdgeGeometry.DistanceTo(edges[context.RouteEdgeIds[next]],nodes[context.RouteNodeIds[next]],nodes[context.RouteNodeIds[next+1]],f.X,f.Y);
            if(!double.IsFinite(deviation)||deviation>policy.MaximumRouteDeviationMeters)return Reject("定位与原路线当前进度不一致，保持路权。");
        }
        var candidates=new List<TrafficSegmentReleaseCandidate>();
        foreach(var lease in context.Leases)
        {
            string? reason=null;
            if(string.IsNullOrWhiteSpace(lease.LeaseId)||lease.TaskId!=context.TaskId||lease.StepId!=context.StepId||lease.VehicleId!=context.VehicleId||lease.MapId!=context.MapId||lease.MapVersionId!=context.MapVersionId)
                reason="租约不属于原车/原步骤/原地图版本。";
            else if(!resources.TryGetValue(lease.ResourceId,out var resource)||resource.Capacity<1||!ValidBoundary(resource.Boundary))reason="资源缺少有效闭合边界，不猜测车体已离开。";
            else
            {
                var last=-1;
                for(int i=0;i<context.RouteEdgeIds.Count;i++)
                {
                    if(edges[context.RouteEdgeIds[i]].ResourceIds.Contains(lease.ResourceId,StringComparer.OrdinalIgnoreCase))last=i;
                    // A non-terminal node resource is passed when the preceding
                    // edge is complete. The current edge need not also finish.
                    // The route's start node (for example the departure waiting
                    // bay) has no preceding edge: it is passed once edge 0 is
                    // complete. The body-envelope check below still applies.
                    if(nodes[context.RouteNodeIds[i]].ResourceIds.Contains(lease.ResourceId,StringComparer.OrdinalIgnoreCase))
                        last=Math.Max(last,Math.Max(i-1,0));
                }
                if(nodes[context.RouteNodeIds[^1]].ResourceIds.Contains(lease.ResourceId,StringComparer.OrdinalIgnoreCase))reason="本段终点资源保持至正常交接/业务收尾。";
                else if(last<0)reason="资源未绑定原路线，无法确定最后一次使用。";
                else if(a.LastFullyTraversedEdgeIndex<last||b.LastFullyTraversedEdgeIndex<last)reason="两帧未确认通过最后一次使用，未来或重入资源仍保持。";
                else if(!Outside(a,body,policy,resource.Boundary!)||!Outside(b,body,policy,resource.Boundary!))reason="完整车体/载荷与定位误差/安全裕量仍覆盖资源。";
            }
            candidates.Add(new(lease.LeaseId,lease.ResourceId,reason==null,reason??"两帧原运输证据与完整车体包络满足评估条件；本次未释放资源。"));
        }
        return new(true,"只读评估；实际释放必须在控制门和数据库事务内重新核对最新证据、租约及未来占用。",candidates)
        {
            Progress = new(a.LastFullyTraversedEdgeIndex,b.LastFullyTraversedEdgeIndex,
                a.X,a.Y,b.X,b.Y,a.PositionUncertaintyMeters,b.PositionUncertaintyMeters,
                body.ProfileId,body.RadiusFromReportedReferenceMeters,a.ObservedAt,b.ObservedAt)
        };
    }
    static bool Finite(double value)=>double.IsFinite(value)&&Math.Abs(value)<=1e6;
    static double Cross(MapPoint2D a,MapPoint2D b,MapPoint2D c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
    static double Distance(MapPoint2D p,MapPoint2D a,MapPoint2D b)
    {
        var x=b.X-a.X;var y=b.Y-a.Y;var length=x*x+y*y;if(length<=1e-16)return Math.Sqrt((p.X-a.X)*(p.X-a.X)+(p.Y-a.Y)*(p.Y-a.Y));
        var t=Math.Clamp(((p.X-a.X)*x+(p.Y-a.Y)*y)/length,0,1);var dx=p.X-a.X-t*x;var dy=p.Y-a.Y-t*y;return Math.Sqrt(dx*dx+dy*dy);
    }
    static bool Intersects(MapPoint2D a,MapPoint2D b,MapPoint2D c,MapPoint2D d)=>
        (Cross(a,b,c)*Cross(a,b,d)<0&&Cross(c,d,a)*Cross(c,d,b)<0)||Distance(a,c,d)<1e-8||Distance(b,c,d)<1e-8||Distance(c,a,b)<1e-8||Distance(d,a,b)<1e-8;
    internal static bool ValidBoundary(IReadOnlyList<MapPoint2D>? polygon)
    {
        if(polygon==null||polygon.Count<3||polygon.Count>256||polygon.Any(p=>p==null||!Finite(p.X)||!Finite(p.Y)))return false;
        double area=0;
        for(int i=0;i<polygon.Count;i++)
        {
            var next=(i+1)%polygon.Count;if(Distance(polygon[i],polygon[next],polygon[next])<1e-8)return false;
            area+=polygon[i].X*polygon[next].Y-polygon[next].X*polygon[i].Y;
            for(int j=i+1;j<polygon.Count;j++)if(j!=next&&(j+1)%polygon.Count!=i&&Intersects(polygon[i],polygon[next],polygon[j],polygon[(j+1)%polygon.Count]))return false;
        }
        return Math.Abs(area)>1e-8;
    }
    static bool Outside(TrafficProgressFrame frame,TrafficBodyEnvelope body,TrafficSegmentReleasePolicy policy,IReadOnlyList<MapPoint2D> polygon)
    {
        var point=new MapPoint2D(frame.X,frame.Y);var radius=body.RadiusFromReportedReferenceMeters+policy.SafetyMarginMeters+frame.PositionUncertaintyMeters;bool inside=false;
        for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
        {
            var a=polygon[j];var b=polygon[i];if(Distance(point,a,b)<=radius+1e-8)return false;
            if((a.Y>point.Y)!=(b.Y>point.Y)&&point.X<(b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
        }
        return !inside;
    }
}
