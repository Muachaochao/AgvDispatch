using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed partial class FleetTestPort
{
    private static bool StandbyStopped(AgvRobotSnapshot v) =>
        v.VelocityX is { } x && Math.Abs(x)<.01 &&
        v.VelocityY is { } y && Math.Abs(y)<.01 &&
        v.AngularVelocity is { } a && Math.Abs(a)<.01 &&
        v.Tasks.All(t=>t.Status is 4 or 5 or 6 or 7);

    private MapNode StandbyNode(string site)
    {
        var current=map.Current??throw new InvalidOperationException("运行地图不存在。");
        var nodes=current.Definition.Nodes.Where(n=>n.Enabled&&(n.Id==site||n.AgvSiteId==site)).ToArray();
        return nodes.Length==1&&!string.IsNullOrWhiteSpace(nodes[0].AgvSiteId)?nodes[0]
            :throw new InvalidOperationException("待机站点映射不唯一或缺少厂家编号："+site);
    }

    public Task<FleetTestStandbyReturn[]> PlanStandbyReturnsAsync(FleetTestRun run,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var config=parameters.Capture();
        if(config.Standbys.Count==0)return Task.FromResult<FleetTestStandbyReturn[]>([]);
        var selected=run.Plan.VehicleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupiedOutside=new HashSet<string>(
            options.VehicleIds.Where(id=>!selected.Contains(id))
                .Select(id=>vehicles.TryGet(id,out var v)?v.CurrentStation:null)
                .Where(site=>!string.IsNullOrWhiteSpace(site))
                .Select(site=>StandbyNode(site!).Id),
            StringComparer.OrdinalIgnoreCase);
        var usedSites=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignments=new List<FleetTestStandbyReturn>();
        foreach(var vehicleId in run.Plan.VehicleIds)
        {
            if(Health(vehicleId,allowLocalTrafficWait:true) is {} health)throw new InvalidOperationException(health);
            if(!vehicles.TryGet(vehicleId,out var vehicle)||string.IsNullOrWhiteSpace(vehicle.CurrentStation)||!StandbyStopped(vehicle))
                throw new InvalidOperationException(vehicleId+"未停稳或当前站点不明，不能规划待机返回。");
            var source=StandbyNode(vehicle.CurrentStation);
            var assignment=config.Standbys
                .Where(b=>b.AllowedVehicleIds.Count==0||b.AllowedVehicleIds.Contains(vehicleId,StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(b=>b.Priority).ThenBy(b=>b.Id,StringComparer.Ordinal)
                .Select(b=>(Bay:b,Node:StandbyNode(b.Site)))
                .FirstOrDefault(candidate=>!usedSites.Contains(candidate.Node.Id)&&
                    !occupiedOutside.Contains(candidate.Node.Id)&&
                    (candidate.Node.Id.Equals(source.Id,StringComparison.OrdinalIgnoreCase)||
                     planner.Plan(source.AgvSiteId!,candidate.Node.AgvSiteId!,new HashSet<string>()) is not null));
            if(assignment.Bay is null||assignment.Node is null)
                throw new InvalidOperationException(vehicleId+"没有可到达、唯一且未被其他车辆占用的待机位。");
            usedSites.Add(assignment.Node.Id);
            var alreadyThere=assignment.Node.Id.Equals(source.Id,StringComparison.OrdinalIgnoreCase);
            assignments.Add(new(
                vehicleId,
                assignment.Bay.Id,
                assignment.Node.AgvSiteId!,
                alreadyThere?FleetTestStandbyReturnStates.Completed:FleetTestStandbyReturnStates.Assigned,
                alreadyThere?"车辆已在配置待机位，无需发送。":"已分配唯一待机位，等待路线核验。",
                null,
                DateTimeOffset.UtcNow)
            {
                Route=alreadyThere?[vehicle.CurrentStation]:[],
                ResourceTaskId=""
            });
        }
        return Task.FromResult(assignments.ToArray());
    }

    public async Task<FleetTestStandbyReturn?> TrySendStandbyReturnAsync(
        FleetTestRun run,FleetTestStandbyReturn[] returns,FleetTestStandbyReturn assignment,
        Action<FleetTestStandbyReturn> persistSendIntent,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(MapExecutionOperation.NewWork,ct);
        if(lease is null)return null;
        var current=map.Current??throw new InvalidOperationException("运行地图不存在。");
        if(current.MapId!=run.MapId||current.VersionId!=run.MapVersionId)
            throw new InvalidOperationException("原测试地图已变化，不能发送待机导航。");
        if(Health(assignment.VehicleId,allowLocalTrafficWait:true) is {} health)
            throw new InvalidOperationException(health);
        if(!vehicles.TryGet(assignment.VehicleId,out var vehicle)||string.IsNullOrWhiteSpace(vehicle.CurrentStation)||!StandbyStopped(vehicle))
            return null;
        if(vehicle.Blocked==true)return null;
        var source=StandbyNode(vehicle.CurrentStation);
        var target=StandbyNode(assignment.Site);
        var route=planner.Plan(source.AgvSiteId!,target.AgvSiteId!,new HashSet<string>());
        if(route is null)throw new InvalidOperationException(assignment.VehicleId+"到待机位"+assignment.Site+"没有可达路线。");
        var path=route.Nodes.Select(id=>StandbyNode(id).AgvSiteId!).ToArray();
        var checkedMap=await preflight.CheckCurrentMapAsync(assignment.VehicleId,run.MapId,run.MapVersionId,ct);
        var checkedRoute=await preflight.CheckRouteAsync(assignment.VehicleId,path,ct);
        if(!checkedMap.Accepted||!checkedRoute.Accepted||!ReferenceEquals(current,map.Current))
            return null;
        if(Health(assignment.VehicleId,allowLocalTrafficWait:true) is {} latestHealth)
            throw new InvalidOperationException(latestHealth);
        var latest=vehicles.TryGet(assignment.VehicleId,out var latestVehicle)?latestVehicle:null;
        if(latest is null||!StandbyStopped(latest)||latest.Blocked==true||
           !string.Equals(latest.CurrentStation,vehicle.CurrentStation,StringComparison.OrdinalIgnoreCase))
            return null;
        // Save the stable vendor identity and every path segment before entering the
        // gateway. A timeout/restart must leave enough evidence to query the original.
        var intent=assignment with
        {
            VendorTaskId="FLEET-STANDBY-"+run.RunId+"-"+assignment.VehicleId,
            Route=path,
            UpdatedAt=DateTimeOffset.UtcNow
        };
        persistSendIntent(intent);
        // Standby returns are sent one vehicle at a time after all business tasks have
        // closed. Avoid synthetic traffic leases because there is no DispatchTask row to
        // reconcile them after a service restart.
        try
        {
            var vendorTaskId=await gateway.CreateTransportTaskAsync(
                intent.VendorTaskId!,assignment.VehicleId,path,
                new AgvTransportAction("Wait"),ct);
            return assignment with
            {
                State=FleetTestStandbyReturnStates.Returning,
                Message="已发送待机导航，车辆返回中。",
                VendorTaskId=vendorTaskId,
                Route=path,
                UpdatedAt=DateTimeOffset.UtcNow
            };
        }
        catch(Exception e)
        {
            // Cancellation can arrive after the vehicle accepted the bytes. Once the
            // gateway send has started it is never evidence that nothing was sent.
            throw new FleetStandbySendUncertainException("待机导航发送结果不确定，车辆可能已收到；禁止自动重发，须核对原车端任务。",e);
        }
    }

    public async Task<FleetTestStandbyReturn> ObserveStandbyReturnAsync(
        FleetTestRun run,FleetTestStandbyReturn assignment,CancellationToken ct)
    {
        if(map.Current?.MapId!=run.MapId||map.Current.VersionId!=run.MapVersionId||
           parameters.Revision!=run.ParameterRevision)
            throw new InvalidOperationException("地图或参数与原待机返回记录不符。");
        if(Health(assignment.VehicleId,allowLocalTrafficWait:true) is {} health)
            throw new InvalidOperationException(health);
        if(string.IsNullOrWhiteSpace(assignment.VendorTaskId))
            throw new InvalidOperationException("原待机导航缺少厂家任务编号。");
        if(!string.IsNullOrWhiteSpace(assignment.ResourceTaskId))
            await resources.RenewTaskAsync(assignment.ResourceTaskId,ct);
        var original=await gateway.GetTransportTaskStatusAsync(assignment.VendorTaskId,assignment.VehicleId,assignment.Route,ct);
        if(original is null||original.VendorTaskId!=assignment.VendorTaskId||
           original.VehicleId!=assignment.VehicleId||!Fresh(original.UpdatedAt))
        {
            throw new InvalidOperationException(assignment.VehicleId+"原待机导航身份或状态不可核对。");
        }
        if(original.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled or AgvTransportTaskState.Suspended)
        {
            throw new InvalidOperationException(assignment.VehicleId+"待机导航未正常完成："+original.State);
        }
        if(original.State==AgvTransportTaskState.Unknown)
            throw new InvalidOperationException(assignment.VehicleId+"原待机导航全链状态未知，保留路权及发送记录待核对。");
        if(original.State!=AgvTransportTaskState.Completed)
            return assignment with
            {
                State=FleetTestStandbyReturnStates.Returning,
                Message="待机导航执行中："+original.State,
                UpdatedAt=DateTimeOffset.UtcNow
            };
        if(!vehicles.TryGet(assignment.VehicleId,out var vehicle)||!Fresh(vehicle.LastSuccessfulPollAt)||
           vehicle.Connection!=ConnectionState.Online||vehicle.Quality!=SignalQuality.Good||
           vehicle.EmergencyStopped!=false||vehicle.Blocked!=false||vehicle.LocalizationConfidence is not >=.5||
           vehicle.Alarms.Any(a=>a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal))
            return assignment with {Message="待机导航已完成，车辆健康状态尚待恢复。",UpdatedAt=DateTimeOffset.UtcNow};
        var target=StandbyNode(assignment.Site);
        var siteMatches=string.Equals(vehicle.CurrentStation,assignment.Site,StringComparison.OrdinalIgnoreCase)||
            (!string.IsNullOrWhiteSpace(vehicle.CurrentStation)&&
             StandbyNode(vehicle.CurrentStation).Id.Equals(target.Id,StringComparison.OrdinalIgnoreCase));
        var positionMatches=vehicle.X is { } x&&vehicle.Y is { } y&&
            double.IsFinite(x)&&double.IsFinite(y)&&
            Math.Sqrt(Math.Pow(x-target.X,2)+Math.Pow(y-target.Y,2))<=.3;
        if(!StandbyStopped(vehicle)||!siteMatches||!positionMatches)
            return assignment with {Message="待机导航已完成，等待车辆停稳并核对实际停靠位置。",UpdatedAt=DateTimeOffset.UtcNow};
        return assignment with
        {
            State=FleetTestStandbyReturnStates.Completed,
            Message="车辆已停稳在配置待机位。",
            UpdatedAt=DateTimeOffset.UtcNow
        };
    }

    public Task ReleaseStandbyReturnAsync(FleetTestStandbyReturn assignment,CancellationToken ct) =>
        string.IsNullOrWhiteSpace(assignment.ResourceTaskId)
            ?Task.CompletedTask
            :resources.ReleaseTaskAsync(assignment.ResourceTaskId,ct);

    public async Task<FleetTestStandbyReturn[]> ReconcileStandbyReturnsAsync(
        FleetTestRun run,CancellationToken ct)
    {
        var updated=new List<FleetTestStandbyReturn>();
        foreach(var assignment in run.StandbyReturns)
        {
            if(string.IsNullOrWhiteSpace(assignment.VendorTaskId))
            {
                if(assignment.State is FleetTestStandbyReturnStates.Sending or FleetTestStandbyReturnStates.Uncertain)
                    throw new InvalidOperationException(assignment.VehicleId+"原待机发送记录缺少厂家任务编号，不能推断原命令或重新发送；请先核对车端任务。");
                updated.Add(assignment);
                continue;
            }
            var observed=await ObserveStandbyReturnAsync(run,assignment with
            {
                State=FleetTestStandbyReturnStates.Returning
            },ct);
            if(observed.State==FleetTestStandbyReturnStates.Completed&&
               !string.IsNullOrWhiteSpace(assignment.ResourceTaskId))
                await resources.ReleaseTaskAsync(assignment.ResourceTaskId,ct);
            updated.Add(observed);
        }
        return updated.ToArray();
    }
}
