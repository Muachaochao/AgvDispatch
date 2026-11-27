using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
namespace AgvDispatch.Infrastructure.Traffic;

// Diagnostic only; no command, receipt, commit or resource write service dependency.
public sealed class TrafficGeometryPreviewService(TrafficSegmentReleaseInspector contextReader,IMapExecutionGate gate,
    TrafficGeometryPreviewPolicy policy,ITrafficGeometryProfileProvider? profiles=null,TimeProvider? clock=null) : ITrafficGeometryPreviewService
{
    public async Task<TrafficGeometryPreviewSnapshot> ReadAsync(TrafficGeometryPreviewQuery query,CancellationToken ct)
    {
        var time=clock??AgvDispatch.Application.Runtime.BusinessClock.Provider;
        string? mapId=null,version=null;
        TrafficGeometryRouteSummary? first=null,second=null;
        TrafficGeometryPreviewSnapshot Refuse(string reason)=>new(time.GetUtcNow(),null,mapId,version,first,second,reason,null);
        if(query is null||string.IsNullOrWhiteSpace(query.FirstStepId)||string.IsNullOrWhiteSpace(query.SecondStepId)||
            query.FirstStepId.Length>200||query.SecondStepId.Length>200||query.FirstStepId==query.SecondStepId)
            return Refuse("请选择两个不同的原运输步骤。");
        if(!policy.Enabled)return Refuse("后台几何只读预检未启用。");
        var policyJson=JsonSerializer.Serialize(policy);
        var started=time.GetUtcNow();
        try
        {
            TrafficSegmentReleaseInspector.GeometryContext a,b;
            using(var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct))
            {
                if(access is null)return Refuse("地图切换或现场恢复门禁关闭，不能评估旧路线。");
                a=await contextReader.ReadGeometryContextAsync(query.FirstStepId,ct);
                b=await contextReader.ReadGeometryContextAsync(query.SecondStepId,ct);
                if(!ReferenceEquals(a.Map,b.Map))return Refuse("读取期间活动地图变化，请重新检查。");
                mapId=a.Map.MapId;version=a.Map.VersionId;first=Summary(a.Context);second=Summary(b.Context);
                if(a.Context.VehicleId==b.Context.VehicleId)return Refuse("须选择两辆不同车辆的原运输。");
            }
            if(profiles is null)return Refuse("原路线已核对，但缺少后台核定车体包络与定位误差界，无法评估。");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var pa=await profiles.ReadAsync(a.Context.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            var pb=await profiles.ReadAsync(b.Context.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            var pa2=await profiles.ReadAsync(a.Context.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            var pb2=await profiles.ReadAsync(b.Context.VehicleId,timeout.Token).WaitAsync(timeout.Token);
            if(pa is null||pb is null||pa!=pa2||pb!=pb2)return Refuse("缺少核定包络或采集期间核定资料变化，请重新检查。");
            using(var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct))
            {
                if(access is null)return Refuse("核定资料读取期间地图或恢复门禁关闭。");
                var aa=await contextReader.ReadGeometryContextAsync(query.FirstStepId,ct);
                var bb=await contextReader.ReadGeometryContextAsync(query.SecondStepId,ct);
                if(a.Fingerprint!=aa.Fingerprint||b.Fingerprint!=bb.Fingerprint||!ReferenceEquals(a.Map,aa.Map)||!ReferenceEquals(a.Map,bb.Map)||
                    policyJson!=JsonSerializer.Serialize(policy))return Refuse("检查期间任务、原路线、地图、占用归属或配置发生变化，请重新检查。");
                var now=time.GetUtcNow();
                if(now<started||now-started>TimeSpan.FromSeconds(5)||!Valid(pa,now)||!Valid(pb,now))
                    return Refuse("核定资料过期、缺版本或检查时钟/时效异常。");
                var result=TrafficGeometryPreviewEvaluator.Evaluate(aa.Map.Definition,mapId!,version!,Route(aa.Context,pa),Route(bb.Context,pb),
                    JsonSerializer.Deserialize<TrafficGeometryPreviewPolicy>(policyJson)!,ct);
                var finished=time.GetUtcNow();var until=new[]{now.AddSeconds(5),pa.VerifiedUntil,pb.VerifiedUntil}.Min();
                if(finished<now||finished>=until)return Refuse("几何计算完成时资料已过期，请重新检查。");
                return new(finished,until,mapId,version,first,second,result.Reason,result);
            }
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Refuse("核定资料读取超时，无法评估。");}
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is InvalidDataException or IOException or JsonException or ArgumentException or InvalidOperationException or TimeoutException){return Refuse(ex.Message);}
    }
    private static bool Valid(TrafficGeometryCertifiedProfile p,DateTimeOffset now)=>p.Body is not null&&!string.IsNullOrWhiteSpace(p.Revision)&&p.VerifiedUntil>now;
    private static TrafficGeometryRouteSummary Summary(TrafficSegmentReleaseContext c)=>new(c.TaskId,c.StepId,c.VehicleId,c.CommandId,c.RouteRevision,c.RouteEdgeIds.Count);
    private static TrafficGeometryRoute Route(TrafficSegmentReleaseContext c,TrafficGeometryCertifiedProfile p)=>
        new(c.TaskId,c.StepId,c.VehicleId,c.MapId,c.MapVersionId,c.CoordinateFrameId,c.RouteNodeIds,c.RouteEdgeIds,p.Body,p.PositionUncertaintyMeters);
}
