using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed partial class ChargingRuntimePort
{
    private async Task<(MapNode Node, string ResourceId)?> WaitingPoint(string id,string vehicleId,CancellationToken ct)
    {
        if(map.Current is not {} current || Node(id) is not { Type:MapNodeType.WaitingPoint, AllowTrafficStop:true, AllowTransitThrough:false } node ||
            options.Sites.Any(site=>Node(site.ChargingSiteId)?.Id==node.Id || Node(site.ExitSiteId)?.Id==node.Id) ||
            dispatch is null || dispatch.GetDumperWaitingSites().Concat(dispatch.StationPickupSites.Values)
                .Append(options.DefaultStandbySiteId).Any(site=>Node(site)?.Id==node.Id) ||
            Node(dispatch.DumperDumpingSite??"")?.Id==node.Id || node.ResourceIds.Count!=1)return null;
        var resource=current.Definition.Resources.SingleOrDefault(item=>item.Id==node.ResourceIds[0]);
        if(resource is not { Type:TrafficResourceType.Station, Capacity:1, Boundary.Count:>=3 } ||
            current.Definition.Nodes.Any(other=>other.Enabled && other.Id!=node.Id && other.ResourceIds.Contains(resource.Id)))return null;
        // The waiting bay must be large enough for the commissioned maximum-load body plus the
        // same release margin used by normal finished-route closure. An unregistered envelope
        // is not sufficient evidence to send a loaded vehicle to that bay.
        var body=envelopes is null?null:await envelopes.ReadAsync(vehicleId,ct);
        if(body is not { CalibrationVerified:true, IncludesMaximumPermittedLoad:true } ||
            !double.IsFinite(body.RadiusFromReportedReferenceMeters) || body.RadiusFromReportedReferenceMeters<=0)return null;
        var center=new MapPoint2D(node.X,node.Y);var radius=body.RadiusFromReportedReferenceMeters+0.15;
        if(!ChargingWaitingGeometry.Outside(center,radius,resource.Boundary))return null;
        return (node,resource.Id);
    }

    public async Task<ChargingWaitingRoute?> PlanWaitingAsync(ChargingSession session,CancellationToken ct)
    {
        if(map.Current is not {} current || Health(session.VehicleId,true) is not null ||
            !vehicles.TryGet(session.VehicleId,out var vehicle) || vehicle.BatteryPercent is not (>=0 and <=100) ||
            Node(vehicle.CurrentStation??"") is not {} source || energy is not {Valid:true} ||
            !energy.Vehicles.TryGetValue(session.VehicleId,out var profile))return null;
        var sites=new List<(MapNode Node,string ResourceId)>();
        foreach(var id in options.WaitingSiteIds)
            if(await WaitingPoint(id,session.VehicleId,ct) is {} point)sites.Add(point);
        if(sites.Any(site=>IsAt(session.VehicleId,site.Node.AgvSiteId!)))return null;
        var unavailable=await resources.GetUnavailableResourceIdsAsync(session.SessionId,ct);
        var predictedWait=session.SiteEstimates.Where(site=>site.StartInSeconds.HasValue)
            .Select(site=>site.StartInSeconds!.Value).DefaultIfEmpty(0).Min();
        if(predictedWait==0 && vehicle.BatteryPercent<=options.CriticalBatteryPercent)return null;
        var candidates=new List<ChargingWaitingRoute>();
        foreach(var (target,parking) in sites)
        {
            if(unavailable.Contains(parking) || OccupiedByOther(session.VehicleId,target))continue;
            var route=planner.PlanForVehicle(source.AgvSiteId!,target.AgvSiteId!,unavailable,session.VehicleId);
            if(route is not {Nodes.Count:>=2} || route.Nodes.Any(id=>Node(id) is null))continue;
            var waitingLeg=Measure(current.Definition,route);
            if(waitingLeg is null)continue;
            var safeCharger=options.Sites.Where(site=>SiteError(site) is null &&
                site.AllowedVehicleIds.Contains(session.VehicleId,StringComparer.OrdinalIgnoreCase)).Any(site=>
                planner.PlanChargingApproachForComparison(target.AgvSiteId!,site.ChargingSiteId) is {} onward &&
                Measure(current.Definition,onward) is {} next && DispatchEnergyForecast.Calculate(vehicle.BatteryPercent!.Value,
                    waitingLeg.Value.Meters,waitingLeg.Value.Seconds+predictedWait,next.Meters,next.Seconds,site.Id,profile,energy).Safe);
            if(!safeCharger)continue;
            candidates.Add(new(current.MapId,current.VersionId,route.Nodes.Select(id=>Node(id)!.AgvSiteId!).ToArray(),parking,
                route.ResourceIds.Append(parking).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),route.Cost));
        }
        return candidates.OrderBy(route=>route.Cost).ThenBy(route=>route.Path[^1],StringComparer.Ordinal).FirstOrDefault();
    }

    public async Task<string?> ValidateWaitingAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.WaitingRoute is not {} route || map.Current is not {} current || current.MapId!=route.MapId ||
            current.VersionId!=route.MapVersionId || await WaitingPoint(route.Path[^1],session.VehicleId,ct) is not {} target ||
            target.ResourceId!=route.ParkingResourceId || !IsAt(session.VehicleId,route.Path[0]) || OccupiedByOther(session.VehicleId,target.Node))
            return "等桩导航的地图、停稳起点或独占停车位核验未通过。";
        var unavailable=await resources.GetUnavailableResourceIdsAsync(session.SessionId,ct);
        var live=planner.PlanForVehicle(route.Path[0],route.Path[^1],unavailable,session.VehicleId);
        if(live is null || !live.Nodes.Select(id=>Node(id)?.AgvSiteId).SequenceEqual(route.Path,StringComparer.OrdinalIgnoreCase) ||
            live.ResourceIds.Any(id=>!route.ResourceIds.Contains(id,StringComparer.OrdinalIgnoreCase)))
            return "等桩进路已变化，请以实时路权重新规划。";
        var check=await preflight.CheckCurrentMapAsync(session.VehicleId,route.MapId,route.MapVersionId,ct);
        if(!check.Accepted)return check.Message;
        check=await preflight.CheckRouteAsync(session.VehicleId,route.Path,ct);
        if(!check.Accepted)return check.Message;
        return ReferenceEquals(current,map.Current)?null:"等桩核验期间运行地图已变化。";
    }

    public async Task<bool> ReserveWaitingAsync(ChargingSession session,CancellationToken ct)
    {
        var route=session.WaitingRoute!;
        var step=session.SessionId+"-PARK";
        await resources.RecordRouteAsync(new(step,session.SessionId,session.VehicleId,route.Path[0],route.Path[^1],[],route.ResourceIds,false,
            "申请等桩进路及独占停车位。") {MapId=route.MapId,MapVersionId=route.MapVersionId},ct);
        return (await resources.TryAcquireAsync(step,session.SessionId,session.VehicleId,route.ResourceIds,ct)).Acquired;
    }

    public async Task<string?> ValidateWaitingRecoveryCloseAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.WaitingRoute is not {} route || map.Current is not {} current || current.MapId!=route.MapId ||
            current.VersionId!=route.MapVersionId || !vehicles.TryGet(session.VehicleId,out var vehicle) ||
            Node(vehicle.CurrentStation??"") is not { AllowTrafficStop:true } actual || !IsAt(session.VehicleId,actual.Id) ||
            route.Path.Any(id=>Node(id)?.Id==actual.Id) || actual.ResourceIds.Any(id=>route.ResourceIds.Contains(id,StringComparer.OrdinalIgnoreCase)))
            return "须确认车辆在原等桩路线之外的可停站点停稳，才能释放等桩资源。";
        if(await BusyReasonAsync(session.VehicleId,session.SessionId,ct) is {} busy)return busy;
        var check=await preflight.CheckCurrentMapAsync(session.VehicleId,route.MapId,route.MapVersionId,ct);
        return check.Accepted && ReferenceEquals(current,map.Current)?null:"原等桩地图核验未通过。";
    }

    public Task TrimWaitingAsync(ChargingSession session,bool departed,CancellationToken ct) =>
        departed?resources.ReleaseStepAsync(session.SessionId+"-PARK",ct):
        resources.ReleaseStepExceptResourcesAsync(session.SessionId+"-PARK",[session.WaitingRoute!.ParkingResourceId],
            "已在经核定的等桩位停稳，保留独占停车资源。",ct);

    public Task ReleaseInboundAsync(ChargingSession session,CancellationToken ct) =>
        resources.ReleaseStepAsync(session.SessionId+"-LOCK",ct);

    public async Task<ChargingApproachEstimate?> EstimateApproachAsync(string vehicleId,ChargingSite site,string sessionId,CancellationToken ct)
    {
        if(map.Current is not {} current || SiteError(site) is not null || !vehicles.TryGet(vehicleId,out var vehicle) ||
            Node(vehicle.CurrentStation??"") is not {} source)return null;
        var unavailable=await resources.GetUnavailableResourceIdsAsync(sessionId,ct);
        var live=planner.PlanChargingApproach(source.AgvSiteId!,site.ChargingSiteId,unavailable,vehicleId);
        if(live is not null && Measure(current.Definition,live) is {} ready)return new(ready.Seconds,0);
        var comparison=planner.PlanChargingApproachForComparison(source.AgvSiteId!,site.ChargingSiteId);
        if(comparison is null || Measure(current.Definition,comparison) is not {} proposed)return null;
        var blocked=comparison.ResourceIds.Any(id=>!id.Equals(site.ResourceId,StringComparison.OrdinalIgnoreCase) && unavailable.Contains(id));
        // A lease expiration is a heartbeat, never evidence of a scheduled release.
        return new(proposed.Seconds,blocked && options.EstimatedApproachWaitSeconds>0?options.EstimatedApproachWaitSeconds:
            !blocked && unavailable.Contains(site.ResourceId)?0:null);
    }

    private static (double Meters,double Seconds)? Measure(MapDefinition map,RoutePlan route)
    {
        var ids=route.EdgeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges=map.Edges.Where(edge=>ids.Contains(edge.Id)).ToDictionary(edge=>edge.Id,StringComparer.OrdinalIgnoreCase);
        if(route.EdgeIds.Count==0 || edges.Count!=ids.Count || edges.Values.Any(edge=>edge.SpeedLimit<=0))return null;
        return (route.EdgeIds.Sum(id=>edges[id].Distance),route.EdgeIds.Sum(id=>edges[id].Distance/edges[id].SpeedLimit));
    }
}

internal static class ChargingWaitingGeometry
{
    public static bool Outside(MapPoint2D point,double radius,IReadOnlyList<MapPoint2D> polygon)
    {
        var inside=false;
        for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
        {
            var a=polygon[j];var b=polygon[i];
            if(Distance(point,a,b)<=radius+1e-8)return false;
            if((a.Y>point.Y)!=(b.Y>point.Y) && point.X<(b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
        }
        return !inside;
    }
    private static double Distance(MapPoint2D p,MapPoint2D a,MapPoint2D b)
    {
        var x=b.X-a.X;var y=b.Y-a.Y;var length=x*x+y*y;
        if(length<=1e-16)return Math.Sqrt((p.X-a.X)*(p.X-a.X)+(p.Y-a.Y)*(p.Y-a.Y));
        var t=Math.Clamp(((p.X-a.X)*x+(p.Y-a.Y)*y)/length,0,1);
        var dx=p.X-a.X-t*x;var dy=p.Y-a.Y-t*y;return Math.Sqrt(dx*dx+dy*dy);
    }
}
