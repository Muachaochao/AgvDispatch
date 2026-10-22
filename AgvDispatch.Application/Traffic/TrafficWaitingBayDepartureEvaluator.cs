using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Application.Traffic;

/// <summary>Read-only, conservative departure evidence. No release receipt or write dependency.</summary>
public static class TrafficWaitingBayDepartureEvaluator
{
    public static TrafficWaitingBayDepartureAssessment Evaluate(TrafficWaitingBayDepartureContext c,
        TrafficProgressPair? evidence,TrafficSegmentReleasePolicy policy,DateTimeOffset now)
    {
        var entry=c.Entry;
        TrafficWaitingBayDepartureAssessment Result(bool available,bool verified,string reason,DateTimeOffset? until=null)=>
            new(now,until,entry.StepId,c.ResourceId,entry.TaskId,entry.VehicleId,entry.MapId,entry.MapVersionId,
                available,verified,reason,c.BayLeases.Select(l=>l.Identity.LeaseId).ToArray());
        var waiting=entry.Map.Nodes.Where(n=>n.Id==c.WaitingNodeId&&n.Enabled).ToArray();
        var dumping=entry.Map.Nodes.Where(n=>n.Id==c.DumpingNodeId&&n.Enabled).ToArray();
        var resources=entry.Map.Resources.Where(r=>r.Id==c.ResourceId).ToArray();
        if(waiting.Length!=1||dumping.Length!=1||c.WaitingNodeId==c.DumpingNodeId||resources.Length!=1||resources[0].Capacity!=1||
            !waiting[0].ResourceIds.Contains(c.ResourceId,StringComparer.OrdinalIgnoreCase)||dumping[0].ResourceIds.Contains(c.ResourceId,StringComparer.OrdinalIgnoreCase)||
            entry.RouteNodeIds.Count<2||entry.RouteNodeIds[0]!=c.WaitingNodeId||entry.RouteNodeIds[^1]!=c.DumpingNodeId)
            return Result(false,false,"原入站路线与独立容量1候车资源、等待/倒料节点不一致。");
        if(c.BayLeases.Count==0||c.BayLeases.Select(l=>l.Identity.LeaseId).Distinct().Count()!=c.BayLeases.Count||
            c.BayLeases.Any(l=>string.IsNullOrWhiteSpace(l.Identity.LeaseId)||string.IsNullOrWhiteSpace(l.Identity.StepId)||
                l.Identity.ResourceId!=c.ResourceId||l.Identity.TaskId!=entry.TaskId||l.Identity.VehicleId!=entry.VehicleId||
                l.Identity.MapId!=entry.MapId||l.Identity.MapVersionId!=entry.MapVersionId||l.ExpiresAt<=now)||
            !entry.Leases.Any(l=>l.ResourceId==c.ResourceId&&c.BayLeases.Any(b=>b.Identity==l)))
            return Result(false,false,"候车位原租约缺失、重复、心跳过期或存在其他任务/车辆/地图归属；保持全部占用。");
        var downstream=dumping[0].ResourceIds.Where(id=>!id.Equals(c.ResourceId,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(c.EntryLeases.Count!=entry.Leases.Count||c.EntryLeases.Select(l=>l.Identity.LeaseId).Distinct().Count()!=c.EntryLeases.Count||
            c.EntryLeases.Any(l=>string.IsNullOrWhiteSpace(l.Identity.LeaseId)||!entry.Leases.Contains(l.Identity)||l.ExpiresAt<=now||l.Identity.TaskId!=entry.TaskId||
                l.Identity.StepId!=entry.StepId||l.Identity.VehicleId!=entry.VehicleId||l.Identity.MapId!=entry.MapId||l.Identity.MapVersionId!=entry.MapVersionId))
            return Result(false,false,"入站与后续资源租约身份或有效期不足，不能据离位坐标推断可交接。");
        if(downstream.Length==0||downstream.Any(id=>entry.Map.Resources.Count(r=>r.Id.Equals(id,StringComparison.OrdinalIgnoreCase)&&r.Capacity>=1&&
                TrafficSegmentReleaseEvaluator.ValidBoundary(r.Boundary))!=1||!entry.Leases.Any(l=>l.ResourceId.Equals(id,StringComparison.OrdinalIgnoreCase)&&
                l.TaskId==entry.TaskId&&l.StepId==entry.StepId&&l.VehicleId==entry.VehicleId&&l.MapId==entry.MapId&&l.MapVersionId==entry.MapVersionId)))
            return Result(false,false,"原入站步骤未完整持有倒料节点的后续资源，不能据离位坐标推断可交接。");
        var assessment=TrafficSegmentReleaseEvaluator.Evaluate(entry,evidence,policy,now);
        if(!assessment.Available)return Result(false,false,assessment.Reason);
        if(evidence!.First.Sequence==long.MaxValue||evidence.Second.Sequence!=evidence.First.Sequence+1)
            return Result(false,false,"候车位离位需相邻两帧权威样本；缺帧或重连不能补造连续离位证据。");
        var candidates=assessment.Candidates.Where(l=>l.ResourceId==c.ResourceId).ToArray();
        if(candidates.Length==0||candidates.Any(l=>!l.EvidenceVerified))
            return Result(true,false,candidates.FirstOrDefault(l=>!l.EvidenceVerified)?.Reason??"原入站步骤没有可核验候车租约。");
        // Never interpret another invalid lease (including downstream ownership) as a permit.
        if(assessment.Candidates.Any(l=>!l.EvidenceVerified&&l.ResourceId!=c.ResourceId&&
            !downstream.Contains(l.ResourceId,StringComparer.OrdinalIgnoreCase)))
            return Result(false,false,"其他原路径资源证据未齐，保守保持候车位。");
        return Result(true,true,"两帧原入站进度及完整车体包络离位证据通过；同任务全部候车租约仍保留，不签发释放许可。",
            new[]{evidence!.First.ObservedAt,evidence.Second.ObservedAt}.Min().AddSeconds(policy.MaximumAgeSeconds));
    }
}
