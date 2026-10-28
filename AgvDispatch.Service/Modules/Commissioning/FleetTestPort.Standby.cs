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
            if(Health(vehicleId) is {} health)throw new InvalidOperationException(health);
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
                ResourceTaskId="FLEET-STANDBY-"+run.RunId+"-"+vehicleId
            });
        }
        return Task.FromResult(assignments.ToArray());
    }

    public async Task<FleetTestStandbyReturn?> TrySendStandbyReturnAsync(
        FleetTestRun run,FleetTestStandbyReturn[] returns,FleetTestStandbyReturn assignment,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(MapExecutionOperation.NewWork,ct);
        if(lease is null)return null;
        var current=map.Current??throw new InvalidOperationException("运行地图不存在。");
        if(current.MapId!=run.MapId||current.VersionId!=run.MapVersionId)
            throw new InvalidOperationException("原测试地图已变化，不能发送待机导航。");
        if(Health(assignment.VehicleId) is {} health)throw new InvalidOperationException(health);
        if(!vehicles.TryGet(assignment.VehicleId,out var vehicle)||string.IsNullOrWhiteSpace(vehicle.CurrentStation)||!StandbyStopped(vehicle))
            return null;
        var source=StandbyNode(vehicle.CurrentStation);
        var target=StandbyNode(assignment.Site);
        var unavailable=await resources.GetUnavailableResourceIdsAsync(assignment.ResourceTaskId,ct);
        var route=planner.Plan(source.AgvSiteId!,target.AgvSiteId!,unavailable);
        if(route is null)
        {
            if(planner.Plan(source.AgvSiteId!,target.AgvSiteId!,new HashSet<string>()) is null)
                throw new InvalidOperationException(assignment.VehicleId+"到待机位"+assignment.Site+"没有可达路线。");
            return null;
        }
        var path=route.Nodes.Select(id=>StandbyNode(id).AgvSiteId!).ToArray();
        var acquisition=await resources.TryAcquireAsync(
            assignment.ResourceTaskId,assignment.ResourceTaskId,assignment.VehicleId,route.ResourceIds,ct);
        if(!acquisition.Acquired)return null;
        var acquired=true;
        try
        {
            var checkedMap=await preflight.CheckCurrentMapAsync(assignment.VehicleId,run.MapId,run.MapVersionId,ct);
            var checkedRoute=await preflight.CheckRouteAsync(assignment.VehicleId,path,ct);
            if(!checkedMap.Accepted||!checkedRoute.Accepted||!ReferenceEquals(current,map.Current))
                throw new InvalidOperationException("待机路线车端核验失败："+checkedMap.Message+checkedRoute.Message);
            var latest=vehicles.TryGet(assignment.VehicleId,out var latestVehicle)?latestVehicle:null;
            if(latest is null||!StandbyStopped(latest)||
               !string.Equals(latest.CurrentStation,vehicle.CurrentStation,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("发送前车辆站点或停车状态变化，车辆未发送。");
            // Keep the resource reservation on an uncertain gateway result. The persisted
            // run is moved to Hold and this intent must be reconciled before another send.
            try
            {
                var vendorTaskId=await gateway.CreateTransportTaskAsync(
                    assignment.ResourceTaskId,assignment.VehicleId,path,new AgvTransportAction("Wait"),ct);
                acquired=false;
                return assignment with
                {
                    State=FleetTestStandbyReturnStates.Returning,
                    Message="已发送待机导航，车辆返回中。",
                    VendorTaskId=vendorTaskId,
                    Route=path,
                    UpdatedAt=DateTimeOffset.UtcNow
                };
            }
            catch(Exception e) when(e is not OperationCanceledException||!ct.IsCancellationRequested)
            {
                throw new FleetStandbySendUncertainException("待机导航发送结果不确定，车辆可能已收到；禁止自动重发，须核对原车端任务。");
            }
        }
        finally
        {
            if(acquired)await resources.ReleaseTaskAsync(assignment.ResourceTaskId,ct);
        }
    }

    public async Task<FleetTestStandbyReturn> ObserveStandbyReturnAsync(
        FleetTestRun run,FleetTestStandbyReturn assignment,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(assignment.VendorTaskId))
            throw new InvalidOperationException("原待机导航缺少厂家任务编号。");
        await resources.RenewTaskAsync(assignment.ResourceTaskId,ct);
        var original=await gateway.GetTransportTaskStatusAsync(assignment.VendorTaskId,ct);
        if(original is null||original.VendorTaskId!=assignment.VendorTaskId||
           original.VehicleId!=assignment.VehicleId||!Fresh(original.UpdatedAt))
        {
            await resources.ReleaseTaskAsync(assignment.ResourceTaskId,ct);
            throw new InvalidOperationException(assignment.VehicleId+"原待机导航身份或状态不可核对。");
        }
        if(original.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled or AgvTransportTaskState.Suspended)
        {
            await resources.ReleaseTaskAsync(assignment.ResourceTaskId,ct);
            throw new InvalidOperationException(assignment.VehicleId+"待机导航未正常完成："+original.State);
        }
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
        resources.ReleaseTaskAsync(assignment.ResourceTaskId,ct);
}
