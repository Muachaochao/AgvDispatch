using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed class ChargingRuntimePort(ChargingOptions options, IAgvStateCache vehicles, IActiveTaskLookup tasks,
    IActiveMapRuntime map, IMapExecutionGate gate, IRoutePlanner planner, ITrafficResourceManager resources,
    ITrafficResourceControlService traffic, IAgvGateway gateway, IRobokitTaskMapPreflight preflight,
    Func<string?>? externalBlock = null, TimeProvider? clock = null, IChargingBusinessSafety? businessSafety = null,
    Func<string,string?>? vehicleBlock = null, Func<string,string?>? returnSite = null) : IChargingRuntimePort
{
    private DateTimeOffset Now => (clock??TimeProvider.System).GetUtcNow();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> planFailures=new(StringComparer.OrdinalIgnoreCase);
    public string? LastPlanFailure(string vehicleId,ChargingSite site)=>planFailures.TryGetValue(vehicleId+"|"+site.Id,out var reason)?reason:null;
    public IReadOnlyList<AgvRobotSnapshot> Vehicles => vehicles.GetAll();
    public string? NewWorkBlockReason => gate.NewWorkBlockReason ?? externalBlock?.Invoke();
    public ValueTask<IDisposable?> EnterExecutionAsync(bool newWork,CancellationToken ct) => gate.EnterAsync(newWork?MapExecutionOperation.NewWork:MapExecutionOperation.ExistingWork,ct);
    public async ValueTask<IDisposable?> EnterRecoveryControlAsync(CancellationToken ct) => await gate.EnterControlAsync(ct);
    private bool Fresh(DateTimeOffset? t)=>t is {} stamp&&stamp<=Now.AddSeconds(1)&&Now-stamp<=TimeSpan.FromSeconds(options.StateMaxAgeSeconds);
    private static bool Stopped(AgvRobotSnapshot v) => v.VelocityX is {} x&&double.IsFinite(x)&&Math.Abs(x)<.01&&
        v.VelocityY is {} y&&double.IsFinite(y)&&Math.Abs(y)<.01&&v.AngularVelocity is {} a&&double.IsFinite(a)&&Math.Abs(a)<.01&&
        v.Tasks.All(t=>t.Status is 4 or 5 or 6 or 7);
    private string? Health(string vehicleId,bool requireStopped)
    {
        if(!vehicles.TryGet(vehicleId,out var v))return "尚无车辆实时状态。";
        if(v.Connection!=ConnectionState.Online||v.Quality!=SignalQuality.Good||!Fresh(v.LastSuccessfulPollAt))return "车辆离线、采集过期或状态质量无效。";
        if(v.EmergencyStopped!=false||v.Blocked!=false||v.LocalizationConfidence is not >=.5||v.X is not {} x||v.Y is not {} y||!double.IsFinite(x)||!double.IsFinite(y))
            return "车辆急停、阻挡、定位或坐标尚未通过充电门禁。";
        if(v.Alarms.Any(a=>a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal))return "车辆存在错误或严重报警，保留现场核对。";
        return requireStopped&&!Stopped(v)?"车辆未确认停稳或仍有未结束的车端任务。":null;
    }
    public async Task<string?> BusyReasonAsync(string vehicleId,string? ownSessionId,CancellationToken ct)
    {
        if(externalBlock?.Invoke() is {} block)return block;
        if(vehicleBlock?.Invoke(vehicleId) is {} vehicleReason)return vehicleReason;
        if((await tasks.GetActiveTasksAsync(ct)).Any(t=>string.Equals(t.VehicleId,vehicleId,StringComparison.OrdinalIgnoreCase)))
            return "当前业务箱体循环尚未闭合，等待任务及设备责任安全结束。";
        if(businessSafety is not null && await businessSafety.GetBlockReasonAsync(vehicleId,ct) is {} unsafeBusiness)return unsafeBusiness;
        var leases=(await traffic.GetResourcesAsync(ct)).SelectMany(r=>r.Leases);
        if(leases.Any(l=>l.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase)&&l.TaskId!=ownSessionId))
            return "车辆仍有其他业务或调试路权责任，不能前往充电。";
        return null;
    }
    private MapNode? Node(string id)
    {
        var definition=map.Current?.Definition;
        return definition is not null&&MapRouteSiteMapper.TryResolveNode(definition,id,out var node)&&
            node is {Enabled:true}&&!string.IsNullOrWhiteSpace(node.AgvSiteId)?node:null;
    }
    private string? SiteError(ChargingSite site)
    {
        var current=map.Current;
        if(current is null)return "没有运行地图。";
        if(!site.Commissioned||site.ControlMode is not ("Relay6005" or "RobokitScript"))
            return "充电位尚未确认现场对接、控制方式及安全退出方式。";
        var target=Node(site.ChargingSiteId);var exit=Node(site.ExitSiteId);
        if(target is not {Type:MapNodeType.ChargingPoint}||exit is null||exit.Id==target.Id)return "充电点类型、出口或厂家站点映射无效。";
        if(exit.ResourceIds.Contains(site.ResourceId,StringComparer.OrdinalIgnoreCase))return "退出点仍属于原充电资源，不能作为实际离位点。";
        var bay=current.Definition.Resources.SingleOrDefault(r=>r.Id.Equals(site.ResourceId,StringComparison.OrdinalIgnoreCase));
        if(bay is not {Type:TrafficResourceType.ChargingBay,Capacity:1}||!target.ResourceIds.Contains(site.ResourceId,StringComparer.OrdinalIgnoreCase))
            return "充电点必须关联容量为1的ChargingBay独占资源。";
        if(current.Definition.Nodes.Any(n=>n.Enabled&&n.Type==MapNodeType.ChargingPoint&&n.Id!=target.Id&&n.ResourceIds.Contains(site.ResourceId,StringComparer.OrdinalIgnoreCase)))
            return "多个充电点共用了同一充电位资源，须先核对配置。";
        return null;
    }
    private bool OccupiedByOther(string vehicleId,MapNode node) => vehicles.GetAll().Any(v=>!v.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase)&&
        (v.CurrentStation?.Equals(node.AgvSiteId,StringComparison.OrdinalIgnoreCase)==true||v.CurrentStation?.Equals(node.Id,StringComparison.OrdinalIgnoreCase)==true||
         v.X is {} x&&v.Y is {} y&&double.IsFinite(x)&&double.IsFinite(y)&&Math.Sqrt(Math.Pow(x-node.X,2)+Math.Pow(y-node.Y,2))<.6));
    public async Task<ChargingRoute?> PlanAsync(string vehicleId,ChargingSite site,CancellationToken ct)
        => await PlanWithReturnAsync(vehicleId,site,null,ct);

    public async Task<ChargingRoute?> PlanWithReturnAsync(string vehicleId,ChargingSite site,string? requestedReturnSite,CancellationToken ct)
    {
        var key=vehicleId+"|"+site.Id;
        ChargingRoute? Fail(string reason){planFailures[key]=reason;return null;}
        if(Health(vehicleId,true) is {} health)return Fail(health);
        if(vehicleBlock?.Invoke(vehicleId) is {} block)return Fail(block);
        if(SiteError(site) is {} siteError)return Fail(siteError);
        if(!vehicles.TryGet(vehicleId,out var v)||Node(v.CurrentStation??"") is not {} source)return Fail("车辆当前不在地图命名站点上，不能作为充电路线起点。");
        var current=map.Current!;var target=Node(site.ChargingSiteId)!;var exit=Node(site.ExitSiteId)!;
        var selectedHome=string.IsNullOrWhiteSpace(requestedReturnSite)?returnSite?.Invoke(vehicleId):requestedReturnSite.Trim();
        // B07-FIX C1: the return point is a dedicated standby. The vehicle's own start point is no longer the default.
        if(string.IsNullOrWhiteSpace(selectedHome))selectedHome=options.DefaultStandbySiteId?.Trim();
        if(string.IsNullOrWhiteSpace(selectedHome))return Fail("未配置专用充电返回待机点（Charging:DefaultStandbySiteId 或业务待机点），禁止以车辆起点或出口代替。");
        var home=Node(selectedHome);
        if(home is null)return Fail($"充电后返回点{selectedHome}不在运行地图上。");
        if(options.Sites.Any(s=>Node(s.ExitSiteId)?.Id==home.Id||Node(s.ChargingSiteId)?.Id==home.Id))return Fail($"返回点{home.Id}是充电出口或充电点，须为专用待机位。");
        if(exit.Id==source.Id||exit.Id==home.Id)return Fail($"充电出口{exit.Id}不能是车辆起点或返回待机点。");
        if(OccupiedByOther(vehicleId,target))return Fail($"充电点{target.Id}被其他车辆占用。");
        // B07-FIX C10: exit and return occupancy is rechecked when each leg is acquired (AcquireExitAsync / AcquireReturnAsync).
        // No same-station empty route can stand in for a commissioned docking move.
        if(source.Id==target.Id)return Fail("车辆已在充电点上，不能用空路线代替对接动作。");
        var unavailable=await resources.GetUnavailableResourceIdsAsync("CHARGING-PLAN",ct);
        if(unavailable.Contains(site.ResourceId))return Fail($"充电位资源{site.ResourceId}已被占用。");
        // B07-FIX C4: legs are planned before the vehicle leaves its bay; its own parked body must not block
        // the way back to that bay (all other vehicles, leases and the dumper clearance still apply).
        var incoming=planner.PlanChargingApproach(source.AgvSiteId!,target.AgvSiteId!,unavailable,vehicleId);
        var outgoing=planner.PlanForVehicle(target.AgvSiteId!,exit.AgvSiteId!,unavailable,vehicleId);
        var returning=planner.PlanForVehicle(exit.AgvSiteId!,home.AgvSiteId!,unavailable,vehicleId);
        if(incoming is not {Nodes.Count:>=2})return Fail($"{source.Id}→{target.Id}在当前可用路权下无路线。");
        if(outgoing is not {Nodes.Count:>=2})return Fail($"{target.Id}→出口{exit.Id}在当前可用路权下无路线。");
        if(returning is not {Nodes.Count:>=2})return Fail($"出口{exit.Id}→返回点{home.Id}在当前可用路权下无路线。");
        if(incoming.Nodes.Any(id=>Node(id) is null)||outgoing.Nodes.Any(id=>Node(id) is null)||returning.Nodes.Any(id=>Node(id) is null))return Fail("充电路线含未映射厂家站点的节点。");
        planFailures.TryRemove(key,out _);
        return new(current.MapId,current.VersionId,incoming.Nodes.Select(id=>Node(id)!.AgvSiteId!).ToArray(),outgoing.Nodes.Select(id=>Node(id)!.AgvSiteId!).ToArray(),
            incoming.ResourceIds.Concat(outgoing.ResourceIds).Concat(returning.ResourceIds).Append(site.ResourceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),incoming.Cost)
        {
            OriginSiteId=source.AgvSiteId,
            ReturnSiteId=home.AgvSiteId,
            ReturnPath=returning.Nodes.Select(id=>Node(id)!.AgvSiteId!).ToArray(),
            // B07-FIX C5: only the inbound road and the bay are reserved up front.
            InboundResourceIds=incoming.ResourceIds.Append(site.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }
    public async Task<string?> ValidateAsync(ChargingSession session,bool requireStopped,CancellationToken ct)
    {
        if(externalBlock?.Invoke() is {} block)return block;
        if(vehicleBlock?.Invoke(session.VehicleId) is {} vehicleReason)return vehicleReason;
        if(Health(session.VehicleId,requireStopped) is {} health)return health;
        if(session.Site is null||session.Route is null||SiteError(session.Site) is {} error)return "原充电位或路线配置尚未核定。";
        var current=map.Current;
        if(current is null||current.MapId!=session.Route.MapId||current.VersionId!=session.Route.MapVersionId)
            return "运行地图已变化，保留原充电会话和路权。";
        if(!session.Site.AllowedVehicleIds.Contains(session.VehicleId,StringComparer.OrdinalIgnoreCase))return "原充电位未确认允许当前车辆。";
        var check=await preflight.CheckCurrentMapAsync(session.VehicleId,session.Route.MapId,session.Route.MapVersionId,ct);
        if(!check.Accepted)return "充电前车端地图核验未通过："+check.Message;
        if(!ReferenceEquals(current,map.Current))return "充电核验期间运行地图发生变化。";
        if(requireStopped)
        {
            if(Health(session.VehicleId,true) is {} changed)return changed;
            // Recheck the exact route in front of every navigation write.
            var path=session.Phase switch
            {
                ChargingPhase.SendingReturn or ChargingPhase.Returning => session.Route.ReturnPath,
                ChargingPhase.AwaitingStop or ChargingPhase.SendingExit or ChargingPhase.Exiting => session.Route.ExitPath,
                _ => session.Route.ToCharger
            };
            if(path.Length<2)return "原充电导航阶段缺少完整路线。";
            var routeCheck=await preflight.CheckRouteAsync(session.VehicleId,path,ct);
            if(!routeCheck.Accepted)return "充电导航路线核验未通过："+routeCheck.Message;
            if(!ReferenceEquals(current,map.Current))return "路线核验期间地图发生变化。";
        }
        return null;
    }
    public async Task<string?> ValidateRecoveryCloseAsync(ChargingSession session,CancellationToken ct)
    {
        // This path performs no motion/relay write and must remain available while
        // the startup recovery barrier or command enablement is closed.
        if(Health(session.VehicleId,true) is {} health)return health;
        if(session.Route is null||session.Site is null||map.Current is not {} current||
           current.MapId!=session.Route.MapId||current.VersionId!=session.Route.MapVersionId)
            return "恢复关闭须先核对原充电地图及版本，禁止跨地图释放。";
        if(await businessSafetyCheck(session.VehicleId,ct) is {} safety)return safety;
        if((await traffic.GetResourcesAsync(ct)).SelectMany(r=>r.Leases).Any(l=>l.VehicleId.Equals(session.VehicleId,StringComparison.OrdinalIgnoreCase)&&l.TaskId!=session.SessionId))
            return "仍有其他任务路权责任，不能关闭充电归属。";
        if((await tasks.GetActiveTasksAsync(ct)).Any(t=>string.Equals(t.VehicleId,session.VehicleId,StringComparison.OrdinalIgnoreCase)))return "仍有未结束业务责任。";
        var check=await preflight.CheckCurrentMapAsync(session.VehicleId,session.Route.MapId,session.Route.MapVersionId,ct);
        return !check.Accepted?check.Message:!ReferenceEquals(current,map.Current)?"核验期间地图已变化。":null;
    }
    private Task<string?> businessSafetyCheck(string vehicleId,CancellationToken ct)=>businessSafety?.GetBlockReasonAsync(vehicleId,ct)??Task.FromResult<string?>(null);
    public bool IsAt(string vehicleId,string siteId)
    {
        if(Health(vehicleId,true) is not null||!vehicles.TryGet(vehicleId,out var v)||Node(siteId) is not {} target||Node(v.CurrentStation??"") is not {} actual||actual.Id!=target.Id)return false;
        return v.X is {} x&&v.Y is {} y&&Math.Sqrt(Math.Pow(x-target.X,2)+Math.Pow(y-target.Y,2))<=.3;
    }
    public async Task<bool> ReserveAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.Route is null||session.Site is null||await ValidateAsync(session,true,ct) is not null||
            await BusyReasonAsync(session.VehicleId,session.SessionId,ct) is not null)return false;
        var route=session.Route;
        // B07-FIX C5: reserve the inbound road and the bay only. The exit and return roads are leased when the vehicle needs them.
        var ids=route.InboundResourceIds.Length>0?route.InboundResourceIds:route.ResourceIds;
        await resources.RecordRouteAsync(new(session.SessionId+"-LOCK",session.SessionId,session.VehicleId,route.ToCharger[0],route.ToCharger[^1],[],ids,false,
            "充电会话独占充电位及来车道路；出口及返程道路在停充后申请。") {MapId=route.MapId,MapVersionId=route.MapVersionId,QueuePriority=ChargingQueuePriority(session)},ct);
        return (await resources.TryAcquireAsync(session.SessionId+"-LOCK",session.SessionId,session.VehicleId,ids,ct)).Acquired;
    }
    // B07-FIX C5: a vehicle at or below the critical battery outranks every ordinary request for the bay, and the lower battery
    // wins among critical vehicles. Ordinary charging keeps priority zero, so business requests are not displaced.
    public const double CriticalChargingQueueTier=1_000_000_000;
    private double ChargingQueuePriority(ChargingSession session)=>
        vehicles.TryGet(session.VehicleId,out var v)&&v.BatteryPercent is {} battery&&battery<=options.CriticalBatteryPercent
            ? CriticalChargingQueueTier+(double)(100m-battery) : 0;
    public Task RenewAsync(string sessionId,CancellationToken ct)=>resources.RenewTaskAsync(sessionId,ct);
    // B07-FIX C6/C8: once the vehicle stands on the charger, only the charging bay and the charging point are kept.
    // C6 kept the exit path too; on B07 the CHARGE-01 exit J0631 is the only approach to the dumper (J0632→J0631→J0651),
    // and an exit into the waiting area would hold the shared collector junctions, so a 40-minute charge stopped every
    // dumping cycle. The exit path is acquired again as ordinary traffic after the stop is verified (AcquireExitAsync).
    public async Task<int> ShrinkToBayAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.Site is null||session.Route is null||map.Current is not {} current||
           current.MapId!=session.Route.MapId||current.VersionId!=session.Route.MapVersionId)return 0;
        var keep=new HashSet<string>(StringComparer.OrdinalIgnoreCase){session.Site.ResourceId};
        if(Node(session.Site.ChargingSiteId) is not {} point)return 0;
        keep.UnionWith(point.ResourceIds);
        return await resources.ReleaseStepExceptResourcesAsync(session.SessionId+"-LOCK",keep,
            "车辆已在充电点停稳：只保留充电位，释放来车道路、出口道路和未使用的返回道路；停充后再按普通交通申请出口。",ct);
    }
    public async Task<bool> AcquireExitAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.Site is null||!SameMap(session)||PathResources(session.Route!.ExitPath) is not {} path)return false;
        // B07-FIX C10: the exit is checked physically when the stop is confirmed; an occupied exit waits without Hold.
        if(path.Nodes.Any(n=>OccupiedByOther(session.VehicleId,n)))return false;
        var ids=path.Ids.Append(session.Site.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var stepId=session.SessionId+"-EXIT";
        await resources.RecordRouteAsync(new(stepId,session.SessionId,session.VehicleId,session.Route!.ExitPath[0],session.Route.ExitPath[^1],[],ids,false,
            "停充已确认：按普通交通申请充电出口路线。") {MapId=session.Route.MapId,MapVersionId=session.Route.MapVersionId},ct);
        return (await resources.TryAcquireAsync(stepId,session.SessionId,session.VehicleId,ids,ct)).Acquired;
    }
    // B07-FIX C1: the return road is leased only after the vehicle has stopped and stands at the exit.
    public async Task<bool> AcquireReturnAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.Route is null||!SameMap(session)||session.Route.ReturnPath.Length<2||PathResources(session.Route.ReturnPath) is not {} path)return false;
        if(path.Nodes.Any(n=>OccupiedByOther(session.VehicleId,n)))return false;
        var stepId=session.SessionId+"-RETURN";
        await resources.RecordRouteAsync(new(stepId,session.SessionId,session.VehicleId,session.Route.ReturnPath[0],session.Route.ReturnPath[^1],[],path.Ids,false,
            "充电已停止并到达出口：按普通交通申请返回待机点路线。") {MapId=session.Route.MapId,MapVersionId=session.Route.MapVersionId},ct);
        if(!(await resources.TryAcquireAsync(stepId,session.SessionId,session.VehicleId,path.Ids,ct)).Acquired)return false;
        // The exit road beyond the return road is no longer needed once the return road is held.
        await resources.ReleaseStepExceptResourcesAsync(session.SessionId+"-EXIT",path.Ids,
            "返程路权已取得：释放出口路线中返程不需要的部分。",ct);
        return true;
    }
    // B07-FIX C1: at the exit the charging bay is no longer needed and is released at once; the exit road stays leased
    // until the return road is held, so the vehicle is never without a lease while it waits at the exit.
    public async Task<int> ReleaseBayAtExitAsync(ChargingSession session,CancellationToken ct)
    {
        if(session.Site is null||session.Route is null||PathResources(session.Route.ExitPath) is not {} exit)return 0;
        var keep=exit.Ids.Where(id=>!id.Equals(session.Site.ResourceId,StringComparison.OrdinalIgnoreCase)).ToArray();
        var released=await resources.ReleaseStepExceptResourcesAsync(session.SessionId+"-LOCK",[],
            "车辆已到达充电出口：充电位不再需要，释放充电位及来车道路。",ct);
        released+=await resources.ReleaseStepExceptResourcesAsync(session.SessionId+"-EXIT",keep,
            "车辆已到达充电出口：释放充电位，保留出口路线直至返程路权取得。",ct);
        return released;
    }
    private bool SameMap(ChargingSession session)=>map.Current is {} current&&session.Route is not null&&
        current.MapId==session.Route.MapId&&current.VersionId==session.Route.MapVersionId;
    private (MapNode[] Nodes,string[] Ids)? PathResources(IReadOnlyList<string> path)
    {
        if(map.Current is not {} current||path.Count<2)return null;
        var nodes=path.Select(id=>Node(id)).ToArray();
        if(nodes.Any(n=>n is null))return null;
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var node in nodes)ids.UnionWith(node!.ResourceIds);
        foreach(var (from,to) in nodes.Zip(nodes.Skip(1)))
            foreach(var edge in current.Definition.Edges.Where(e=>e.Enabled&&(e.From.Equals(from!.Id,StringComparison.OrdinalIgnoreCase)&&e.To.Equals(to!.Id,StringComparison.OrdinalIgnoreCase)||
                e.Bidirectional&&e.From.Equals(to!.Id,StringComparison.OrdinalIgnoreCase)&&e.To.Equals(from.Id,StringComparison.OrdinalIgnoreCase))))
                ids.UnionWith(edge.ResourceIds);
        return (nodes.Select(n=>n!).ToArray(),ids.ToArray());
    }
    public Task ReleaseAsync(string sessionId,CancellationToken ct)=>resources.ReleaseTaskAsync(sessionId,ct);
    public Task<string> NavigateAsync(string commandId,string vehicleId,IReadOnlyList<string> path,CancellationToken ct)=>
        gateway.CreateTransportTaskAsync(commandId,vehicleId,path,new AgvTransportAction("Wait"),ct);
    public Task CancelNavigationAsync(ChargingSession session,CancellationToken ct)
    {
        var taskId=session.VendorTaskId??session.CommandId;
        return string.IsNullOrWhiteSpace(taskId)
            ? Task.FromException(new InvalidOperationException("原充电导航任务身份未知，不能取消。"))
            : gateway.CancelTaskAsync(taskId,ct);
    }
    public Task<AgvTransportTaskStatus?> ObserveNavigationAsync(string commandId,string vehicleId,IReadOnlyList<string> path,CancellationToken ct)=>
        gateway.GetTransportTaskStatusAsync(commandId,vehicleId,path,ct);
}
