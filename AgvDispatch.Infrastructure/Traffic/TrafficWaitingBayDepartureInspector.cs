using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Traffic;

// Diagnostic dependency graph deliberately contains no resource manager, receipt or write service.
public sealed class TrafficWaitingBayDepartureInspector(ISqlSugarClient database,
    TrafficSegmentReleaseInspector contextReader,IMapExecutionGate gate,Func<TrafficWaitingBaySites> sites,
    IAuthoritativeTrafficProgressReader? progress=null,ITrafficGeometryProfileProvider? profiles=null,
    TimeProvider? clock=null) : ITrafficWaitingBayDepartureService
{
    private static readonly TrafficSegmentReleasePolicy EvidencePolicy=new(){Enabled=true};
    public async Task<TrafficWaitingBayDepartureAssessment> ReadAsync(TrafficWaitingBayDepartureQuery query,CancellationToken ct)
    {
        var time=clock??TimeProvider.System;var started=time.GetUtcNow();
        TrafficSegmentReleaseContext? original=null;
        TrafficWaitingBayDepartureAssessment Refuse(string reason)=>new(time.GetUtcNow(),null,query?.StepId??"",query?.ResourceId??"",
            original?.TaskId,original?.VehicleId,original?.MapId,original?.MapVersionId,false,false,reason,[]);
        if(query is null||string.IsNullOrWhiteSpace(query.StepId)||query.StepId.Length>64||
            string.IsNullOrWhiteSpace(query.ResourceId)||query.ResourceId.Length>128)return Refuse("原入站步骤或候车资源编号无效。");
        try
        {
            var configuration=sites();Capture before;
            using(var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct))
            {
                if(access is null)return Refuse("地图切换或恢复门禁关闭，候车位继续保持。");
                before=await LoadAsync(query,configuration,ct);original=before.Route.Context;
            }
            if(progress is null||profiles is null)return Refuse("缺少已核定的原命令连续进度源或有效车体/载荷与定位误差资料；不从当前站点推断离位。");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var profile=await profiles.ReadAsync(original.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            var evidence=await progress.ReadAsync(original.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            var repeated=await profiles.ReadAsync(original.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            using(var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct))
            {
                if(access is null)return Refuse("采集期间地图或恢复门禁关闭。");
                var after=await LoadAsync(query,configuration,ct);
                if(configuration!=sites()||before.Route.Fingerprint!=after.Route.Fingerprint||before.Owners!=after.Owners||
                    !ReferenceEquals(before.Route.Map,after.Route.Map))return Refuse("采集期间配置、原命令、地图或候车位全部租约归属发生变化，请重新检查。");
                var now=time.GetUtcNow();
                if(now<started||now-started>TimeSpan.FromSeconds(5)||profile is null||profile!=repeated||profile.Body is null||
                    string.IsNullOrWhiteSpace(profile.Revision)||profile.VerifiedUntil<=now||
                    !double.IsFinite(profile.PositionUncertaintyMeters)||profile.PositionUncertaintyMeters<0||
                    profile.PositionUncertaintyMeters>EvidencePolicy.MaximumUncertaintyMeters)
                    return Refuse("核定资料缺失、过期、变化或采集时效异常。");
                // Use at least the certified error bound, never shrink it to a smaller live estimate.
                if(evidence is not null&&(evidence.First is null||evidence.Second is null))return Refuse("两帧原命令证据不完整。");
                if(evidence is not null)evidence=evidence with{
                    First=evidence.First with{PositionUncertaintyMeters=Math.Max(evidence.First.PositionUncertaintyMeters,profile.PositionUncertaintyMeters)},
                    Second=evidence.Second with{PositionUncertaintyMeters=Math.Max(evidence.Second.PositionUncertaintyMeters,profile.PositionUncertaintyMeters)}};
                var context=new TrafficWaitingBayDepartureContext(after.Route.Context with{Body=profile.Body},after.WaitingNode,
                    after.DumpingNode,query.ResourceId,after.BayLeases,after.EntryLeases);
                var result=TrafficWaitingBayDepartureEvaluator.Evaluate(context,evidence,EvidencePolicy,now);
                var finished=time.GetUtcNow();
                var until=result.ValidUntil is {} valid?new[]{valid,profile.VerifiedUntil}.Min():(DateTimeOffset?)null;
                if(finished<now||finished-started>TimeSpan.FromSeconds(5)||until is {} limit&&finished>=limit)
                    return Refuse("评估完成时证据已过期或时钟异常，请重新检查。");
                return result with{CheckedAt=finished,ValidUntil=until};
            }
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Refuse("离位证据读取超时，占用保持。");}
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is InvalidDataException or IOException or JsonException or ArgumentException or InvalidOperationException or TimeoutException)
        {return Refuse(ex.Message);}
    }
    private async Task<Capture> LoadAsync(TrafficWaitingBayDepartureQuery query,TrafficWaitingBaySites configuration,CancellationToken ct)
    {
        var step=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.StepId==query.StepId).FirstAsync(ct);
        if(step is null||step.StepType!=(int)DispatchTaskStepType.EnterDumpingPosition)
            throw new InvalidDataException("须选择原进入倒料位运输步骤，送到等待位或返程不是离位证据。");
        var route=await contextReader.ReadGeometryContextAsync(query.StepId,ct);
        var waiting=Resolve(route.Context.Map,configuration.WaitingSite);var dumping=Resolve(route.Context.Map,configuration.DumpingSite);
        var resource=route.Context.Map.Resources.Where(r=>r.Id==query.ResourceId).ToArray();
        if(resource.Length!=1||resource[0].Capacity!=1||waiting.Id==dumping.Id||
            !waiting.ResourceIds.Contains(query.ResourceId,StringComparer.OrdinalIgnoreCase)||dumping.ResourceIds.Contains(query.ResourceId,StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("所选资源不是当前配置等待节点的独立容量1候车位。");
        var normalized=query.ResourceId.ToLowerInvariant();
        var bay=await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>SqlFunc.ToLower(l.ResourceId)==normalized).ToListAsync(ct);
        var entry=await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.OwnerStepId==query.StepId).ToListAsync(ct);
        var ownerIds=bay.Select(l=>l.OwnerStepId).Distinct().ToArray();
        var ownersSteps=await database.Queryable<DispatchTaskStepEntity>().Where(s=>ownerIds.Contains(s.StepId)).ToListAsync(ct);
        if(ownersSteps.Count!=ownerIds.Length||ownersSteps.Any(s=>s.TaskId!=route.Context.TaskId||s.VehicleId!=route.Context.VehicleId||
            !(s.StepId==query.StepId&&s.StepType==(int)DispatchTaskStepType.EnterDumpingPosition&&s.Status==(int)DispatchTaskStepStatus.Executing||
              s.StepType==(int)DispatchTaskStepType.DeliverFullBoxToDumper&&s.Status==(int)DispatchTaskStepStatus.Completed)))
            throw new InvalidDataException("候车位保留租约的原步骤归属或送箱/入站阶段不完整。");
        // Acquisition identity is significant; normal renewals are rechecked for expiry, not treated as new owners.
        var owners=JsonSerializer.Serialize(new{
            Leases=bay.Concat(entry).OrderBy(l=>l.LeaseId,StringComparer.Ordinal).Select(l=>new{
                l.LeaseId,l.ResourceId,l.TaskId,l.OwnerStepId,l.VehicleId,l.MapId,l.MapVersionId,l.AcquiredAt}),
            Steps=ownersSteps.OrderBy(s=>s.StepId,StringComparer.Ordinal).Select(s=>new{s.StepId,s.TaskId,s.VehicleId,s.StepType,s.Status})});
        return new(route,waiting.Id,dumping.Id,bay.Select(Lease).ToArray(),entry.Select(Lease).ToArray(),owners);
    }
    private static MapNode Resolve(MapDefinition map,string site)
    {
        var matches=map.Nodes.Where(n=>n.Enabled&&(n.Id.Equals(site,StringComparison.OrdinalIgnoreCase)||
            string.Equals(n.AgvSiteId,site,StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length==1?matches[0]:throw new InvalidDataException("等待/倒料配置不能唯一映射当前地图。");
    }
    private static TrafficWaitingBayLease Lease(TrafficResourceLeaseEntity l)=>new(new(l.LeaseId,l.ResourceId,l.TaskId,
        l.OwnerStepId,l.VehicleId,l.MapId??"",l.MapVersionId??""),new DateTimeOffset(l.ExpiresAt));
    private sealed record Capture(TrafficSegmentReleaseInspector.GeometryContext Route,string WaitingNode,string DumpingNode,
        IReadOnlyList<TrafficWaitingBayLease> BayLeases,IReadOnlyList<TrafficWaitingBayLease> EntryLeases,string Owners);
}
