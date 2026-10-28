using System.Net.Http.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class FleetLabSessionMissingException(string message):InvalidOperationException(message);

public sealed class FleetTestPort(FleetTestOptions options,IBusinessRunService single,IBusinessRunPort business,
    IManualTaskCreationService manual,ITaskCenterQueryService tasks,IDispatchTaskRepository repository,
    IStationStateService stations,IPlcSignalCache signals,IPlcGateway plc,IAgvStateCache vehicles,
    IActiveMapRuntime map,IMapExecutionGate gate,TaskParameterRuntime parameters,DispatchOrchestratorOptions dispatch,
    DispatchTaskCreationOptions creation,HandshakeExecutionOptions handshake,IRobokitTaskMapPreflight preflight,
    ITrafficResourceControlService traffic,bool simulatorOnly) : IFleetTestPort
{
    private static readonly HttpClient lab=new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false})
        {BaseAddress=new Uri("http://127.0.0.1:10201/"),Timeout=TimeSpan.FromSeconds(5)};
    private static bool Fresh(DateTimeOffset? at)=>at is {} t&&t<=DateTimeOffset.UtcNow.AddSeconds(1)&&DateTimeOffset.UtcNow-t<TimeSpan.FromSeconds(3);
    private string? Health(string id)
    {
        if(!vehicles.TryGet(id,out var v))return id+"尚未取得车辆状态。";
        var sample=$"连接={v.Connection}，质量={v.Quality}，最近成功采集={v.LastSuccessfulPollAt:O}，阻挡={v.Blocked}，急停={v.EmergencyStopped}，置信度={v.LocalizationConfidence:0.###}";
        if(!Fresh(v.LastSuccessfulPollAt)||v.Connection!=ConnectionState.Online||v.Quality!=SignalQuality.Good)
            return id+"通信离线、采集质量无效或数据超过3秒未更新。"+sample;
        var reasons=new List<string>();
        if(v.Blocked!=false)reasons.Add("车辆当前被阻挡，需现场清除障碍并在车端解除阻挡");
        if(v.EmergencyStopped!=false)reasons.Add("急停未复位");
        if(v.LocalizationConfidence is not >=.5)reasons.Add($"定位置信度{(v.LocalizationConfidence?.ToString("0.###")??"缺失")}低于0.5");
        var alarms=v.Alarms.Where(a=>a.Severity is AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error or AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Fatal)
            .Select(a=>string.IsNullOrWhiteSpace(a.Code)?"车端报警":a.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(alarms.Length>0)reasons.Add("开放车端报警"+string.Join('、',alarms));
        if(reasons.Count>0)return id+"："+string.Join("；",reasons)+"。"+sample;
        return null;
    }
    public static string? StartingPickupOccupancyConflict(
        IReadOnlyDictionary<string,string> vehicleSites,
        IReadOnlyDictionary<string,string> selectedPickupSites)
    {
        foreach(var pickup in selectedPickupSites)
        {
            var occupants=vehicleSites.Where(v=>string.Equals(v.Value,pickup.Value,StringComparison.OrdinalIgnoreCase))
                .Select(v=>v.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if(occupants.Length>0)
                return $"{pickup.Key}的取货点{pickup.Value}已被参与车辆{string.Join('、',occupants)}占用。请先把参与车辆移到各自独立且不占用所选取货点、等待位或倒料位的配置停车点后再启动。";
        }
        return null;
    }
    public static string? SelectionError(FleetTestStart plan,IReadOnlyCollection<string> configuredVehicles,
        IReadOnlyDictionary<string,string[]> stationPools)
    {
        var allowed=configuredVehicles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(plan.VehicleIds.Any(id=>!allowed.Contains(id)))return "参与车辆超出本次启动配置，请重新读取车辆列表。";
        bool Serves(string station,string vehicle)=>!stationPools.TryGetValue(station,out var ids)||ids.Contains(vehicle,StringComparer.OrdinalIgnoreCase);
        foreach(var station in plan.StationIds)
            if(!plan.VehicleIds.Any(id=>Serves(station,id)))return $"工位{station}的车辆池没有本组参与车辆，请调整工位或车辆选择。";
        foreach(var vehicle in plan.VehicleIds)
            if(!plan.StationIds.Any(station=>Serves(station,vehicle)))return $"车辆{vehicle}不属于任何所选工位的车辆池。";
        return null;
    }
    private async Task<TaskCenterRow[]> AllAsync(CancellationToken ct)
    {
        var result=new List<TaskCenterRow>();
        for(var page=1;page<=10000;page++)
        {var batch=await tasks.QueryAsync(new(TaskListFilter.All,"",page,100),ct);result.AddRange(batch.Items);if(page*100>=batch.TotalCount)return result.ToArray();}
        throw new InvalidOperationException("任务分页未能完整核对。");
    }
    private void Configuration(bool requireNewWorkGate=true)
    {
        if(options.ConfigurationError(dispatch.MaxConcurrentTasks) is {} configurationError)
            throw new InvalidOperationException(configurationError);
        if(!simulatorOnly||!creation.Enabled||creation.AutomaticCreationEnabled||!dispatch.Enabled||
            !handshake.Enabled||handshake.PlcContractVersion!=HandshakeExecutionOptions.Version)
            throw new InvalidOperationException("多车联调须使用本机模拟PLC、启用调度及当前28阶段契约，并关闭独立自动建单器。");
        if(requireNewWorkGate&&!gate.CanStartNewWork)throw new InvalidOperationException(gate.NewWorkBlockReason??"启动恢复尚未放行。");
        if(DumperWaitingBaySafety.GetError(map.Current?.Definition,dispatch,true) is {} error)throw new InvalidOperationException(error);
    }
    private void PlcHealthy()
    {
        var rows=signals.GetAll();
        foreach(var station in parameters.Capture().Pickups.Select(p=>p.StationId).Append(parameters.Capture().DumperStationId))
        {
            var group=rows.Where(r=>r.StationId==station).ToArray();
            if(group.Length==0||group.Any(r=>r.Quality!=SignalQuality.Good||!Fresh(r.UpdatedAt)||r.Value==null)||
                !group.Any(r=>r.VariableName=="Faulted"&&r.Value==false)||
                station!=parameters.Capture().DumperStationId&&!group.Any(r=>r.VariableName=="CommunicationOnline"&&r.Value==true))
                throw new InvalidOperationException(station+"点位质量、新鲜度或联锁无效。");
        }
    }
    public async Task<(string MapId,string Version,string Revision)> ValidateAsync(FleetTestStart plan,CancellationToken ct)
    {
        Configuration();PlcHealthy();
        if(SelectionError(plan,options.VehicleIds,dispatch.StationVehiclePools) is {} selectionError)throw new InvalidOperationException(selectionError);
        if((await single.ReadAsync(ct)).Current is {Stage:not "Completed"})throw new InvalidOperationException("单车原运行尚未闭合。");
        if((await AllAsync(ct)).Any(t=>t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled)))
            throw new InvalidOperationException("启动前须处理原业务任务。");
        var choices=await manual.GetOptionsAsync(ct);
        var configured=parameters.Capture().Pickups.Select(p=>p.StationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(plan.StationIds.Any(id=>!configured.Contains(id)))throw new InvalidOperationException("所选工位未配置取箱站点，请重新读取工位列表。");
        if(BusinessRunPort.ConfigurationError(choices,plan.StationIds) is {} error)throw new InvalidOperationException(error);
        var sites=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current=map.Current??throw new InvalidOperationException("运行地图不存在。");
        foreach(var id in plan.VehicleIds)
        {
            if(Health(id) is {} block)throw new InvalidOperationException(block);
            var choice=choices.Vehicles.SingleOrDefault(v=>string.Equals(v.VehicleId,id,StringComparison.OrdinalIgnoreCase));
            if(choice is not {Available:true}||!sites.Add(MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition,choice.CurrentSite)))throw new InvalidOperationException(id+"不可派发或参与车辆共用同一初始停车点："+choice?.Explanation);
            if(dispatch.GetDumperWaitingSites().Append(dispatch.DumperDumpingSite).Any(site=>
                site is not null&&MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition,site)==MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition,choice.CurrentSite)))
                throw new InvalidOperationException(id+"初始停车点占用倒料等待位或倒料位，请移至独立配置停车点。");
            vehicles.TryGet(id,out var state);
            if(state!.VelocityX is not {} vx||state.VelocityY is not {} vy||state.AngularVelocity is not {} va||Math.Abs(vx)>=.01||Math.Abs(vy)>=.01||Math.Abs(va)>=.01||state.Tasks.Any(t=>t.Status is not (4 or 5 or 6 or 7)))
                throw new InvalidOperationException(id+"尚未停稳或仍有车端任务。");
            foreach(var pickup in parameters.Capture().Pickups.Where(p=>plan.StationIds.Contains(p.StationId,StringComparer.OrdinalIgnoreCase)&&dispatch.AllowsVehicle(p.StationId,id)))
            foreach(var waiting in dispatch.GetDumperWaitingSites())
            {
                var check=await preflight.CheckAsync(id,choice.CurrentSite,pickup.PickupSite,waiting,dispatch.DumperDumpingSite,current.MapId,current.VersionId,ct);
                if(!check.Accepted)throw new InvalidOperationException(id+"车端地图/路线核验失败："+check.Message);
            }
        }
        var vehicleSites=plan.VehicleIds.ToDictionary(id=>id,id=>MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition,choices.Vehicles.Single(v=>string.Equals(v.VehicleId,id,StringComparison.OrdinalIgnoreCase)).CurrentSite!),StringComparer.OrdinalIgnoreCase);
        var pickupSites=parameters.Capture().Pickups.Where(p=>plan.StationIds.Contains(p.StationId,StringComparer.OrdinalIgnoreCase))
            .ToDictionary(p=>p.StationId,p=>MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition,p.PickupSite),StringComparer.OrdinalIgnoreCase);
        if(StartingPickupOccupancyConflict(vehicleSites,pickupSites) is {} occupied)throw new InvalidOperationException(occupied);
        await BaselineAsync(plan.StationIds,true,ct);
        if(!ReferenceEquals(current,map.Current))throw new InvalidOperationException("核验期间地图已改变。");
        return(current.MapId,current.VersionId,parameters.Revision);
    }
    public async Task CheckCurrentAsync(FleetTestRun run,CancellationToken ct)
    {
        Configuration();PlcHealthy();
        if(SelectionError(run.Plan,options.VehicleIds,dispatch.StationVehiclePools) is {} selectionError)throw new InvalidOperationException(selectionError);
        if(map.Current?.MapId!=run.MapId||map.Current.VersionId!=run.MapVersionId||parameters.Revision!=run.ParameterRevision)
            throw new InvalidOperationException("地图或参数与原测试记录不符。");
        foreach(var id in run.Plan.VehicleIds)
        {
            if(Health(id) is {} block)throw new InvalidOperationException(block);
            var mapCheck=await preflight.CheckCurrentMapAsync(id,run.MapId,run.MapVersionId,ct);
            if(!mapCheck.Accepted)throw new InvalidOperationException(id+"车端地图已变化，不能按原测试记录继续："+mapCheck.Message);
        }
        var rows=await AllAsync(ct);
        foreach(var row in rows.Where(t=>t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled)))
        {
            var call=run.Calls.SingleOrDefault(c=>string.Equals(c.StationId,row.StationId,StringComparison.OrdinalIgnoreCase)&&c.Released);
            if(call==null||call.TaskId!=null&&call.TaskId!=row.TaskId||row.CreatedAt<run.StartedAt||row.RequestedVehicleId!=null)
                throw new InvalidOperationException("存在不属于本次多车测试的任务，保持暂停。");
            if(row.VehicleId!=null&&!run.Plan.VehicleIds.Contains(row.VehicleId,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("任务分配到了本次范围外车辆。");
            if(row.Stage is DispatchTaskStage.Faulted or DispatchTaskStage.CancellationPending or DispatchTaskStage.CancellationReview)
                throw new InvalidOperationException("原任务异常或正在取消，须核对恢复。");
        }
    }
    public async Task<FleetTestStatus> ObserveAsync(FleetTestRun? run,CancellationToken ct)
    {
        var details=new List<TaskCenterDetail>();
        if(run!=null)foreach(var call in run.Calls.Where(c=>c.TaskId!=null))
            if(await tasks.GetDetailAsync(call.TaskId!,ct) is {} detail)details.Add(detail);
        return new(options.Enabled,options.VehicleIds,run,options.VehicleIds.Select(id=>
        {vehicles.TryGet(id,out var v);var health=Health(id);return new FleetTestVehicle(id,v?.CurrentStation,health==null,health??"状态数据有效");}).ToArray(),details.ToArray(),DateTimeOffset.UtcNow)
        {ConfiguredStations=parameters.Capture().Pickups.Select(p=>p.StationId).ToArray(),MaxConcurrentTasks=dispatch.MaxConcurrentTasks};
    }
    public async Task LabAsync(FleetTestRun run,string action,string[] stations,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"fleet")
        {Content=JsonContent.Create(new{runId=run.RunId,action,stations,
            taskId=action=="bind"?run.Calls.Single(c=>c.StationId==stations.Single()).TaskId:null,
            recoveryReviewed=action=="check"})};
        request.Headers.Add("X-Agv-Lab-Control","business-run-v1");
        using var response=await lab.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)
        {
            var message=await response.Content.ReadAsStringAsync(ct);
            if(response.StatusCode==System.Net.HttpStatusCode.Conflict&&(message.Contains("模拟器原双车会话不存在",StringComparison.Ordinal)||message.Contains("模拟器原多车会话不存在",StringComparison.Ordinal)))
                throw new FleetLabSessionMissingException(message);
            throw new InvalidOperationException("模拟器未接受："+message);
        }
    }
    public async Task<string?> CreateAsync(FleetTestRun run,string station,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(MapExecutionOperation.NewWork,ct);
        if(lease==null)throw new InvalidOperationException("新任务门禁未放行。");
        var originals=(await AllAsync(ct)).Where(t=>string.Equals(t.StationId,station,StringComparison.OrdinalIgnoreCase)&&t.CreatedAt>=run.StartedAt).ToArray();
        if(originals.Length>1)throw new InvalidOperationException("工位存在多笔任务，不能推断原归属。");
        if(originals.Length==1)
        {
            if(originals[0].Stage is not (DispatchTaskStage.Created or DispatchTaskStage.WaitingForVehicle)||originals[0].VehicleId!=null||originals[0].RequestedVehicleId!=null)
                throw new InvalidOperationException("未绑定的原申请已有执行或终结记录，不能推断归属并重置应答。");
            return originals[0].TaskId;
        }
        if(!stations.TryGet(station,out var state)||!state.CanCreateTask)return null;
        return (await repository.TryCreateAsync(station,creation.DumperStationId,creation.DefaultPriority,ct)).Task.TaskId;
    }
    public async Task AcknowledgeAsync(string station,string taskId,CancellationToken ct)
    {
        using var execution=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct);
        if(execution==null)throw new InvalidOperationException("原任务执行门禁未放行。");
        var detail=await tasks.GetDetailAsync(taskId,ct);
        if(detail==null||!string.Equals(detail.Task.StationId,station,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("受理应答的原任务与工位不符。");
        if(detail.Task.Stage!=DispatchTaskStage.Created)return;
        await plc.WriteSignalAsync(station,"FullBoxRequestAcknowledged",true,ct);await repository.MarkAcknowledgedAsync(taskId,ct);
    }
    public async Task FeedbackAsync(FleetTestRun run,TaskCenterDetail detail,CancellationToken ct)
    {
        if(!run.Calls.Any(c=>c.TaskId==detail.Task.TaskId&&string.Equals(c.StationId,detail.Task.StationId,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("原任务身份不符。");
        await LabAsync(run,"bind",[detail.Task.StationId],ct);
        if(detail.Task.Stage==DispatchTaskStage.Created)
        {
            await AcknowledgeAsync(detail.Task.StationId,detail.Task.TaskId,ct);
            return;
        }
        if(detail.Task.VehicleId is not {} id)return;
        if(!run.Plan.VehicleIds.Contains(id,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("反馈车辆超出本轮范围。");
        var fake=new BusinessRunRecord(run.RunId,new(run.Plan.RequestId,"Cycle",id,[detail.Task.StationId],1,"",true),run.MapId,run.MapVersionId,
            run.ParameterRevision,"多车测试","Executing","",0,detail.Task.TaskId,null,null,run.StopRequested,run.StartedAt);
        await ((BusinessRunPort)business).ApplySimulatorFeedbackAsync(fake,detail,"fleet-feedback",ct);
    }
    private async Task BaselineAsync(IEnumerable<string> requiredStationIds,bool requireStationBoxes,CancellationToken ct)
    {
        PlcHealthy();
        var required=requiredStationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var station in parameters.Capture().Pickups.Select(p=>p.StationId))
        {
            foreach(var name in new[]{"FullBoxRequested","FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"})
                if(signals.GetAll().SingleOrDefault(s=>s.StationId==station&&s.VariableName==name)?.Value!=false)throw new InvalidOperationException(station+"旧呼叫/返箱责任未闭合。");
            // FleetTest is simulator-only. A low BoxPresent is an initial state signal,
            // not proof of a missing physical box. The reviewed simulator injects the
            // simulated box before raising the call; a later Pickup/Place feedback owns
            // the high/low transitions.
            if(!simulatorOnly&&requireStationBoxes&&required.Contains(station)&&
                signals.GetAll().SingleOrDefault(s=>s.StationId==station&&s.VariableName=="BoxPresent")?.Value!=true)
                throw new InvalidOperationException(station+"未确认有箱。");
        }
        foreach(var name in new[]{"AgvAtWaitingPosition","AgvAtDumpingPosition","DumperReady","DumpingCompletedMayLeave"})
            if(signals.GetAll().SingleOrDefault(s=>s.StationId==parameters.Capture().DumperStationId&&s.VariableName==name)?.Value!=false)
                throw new InvalidOperationException("共享倒料握手未闭合。");
        if((await traffic.GetResourcesAsync(ct)).Any(r=>r.Leases.Count>0))throw new InvalidOperationException("仍有交通资源持有记录。");
    }
    public async Task CheckClosedAsync(FleetTestRun run,CancellationToken ct)
    {
        // Closing an already-stopped run is an existing-work reconciliation. It must
        // remain possible while the separate new-work recovery gate is still closed.
        Configuration(requireNewWorkGate:false);PlcHealthy();
        foreach(var id in run.Plan.VehicleIds)
            if(Health(id) is {} block)throw new InvalidOperationException(block);
        if((await AllAsync(ct)).Any(t=>t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled)))throw new InvalidOperationException("原任务尚未全部闭合。");
        // A stopped/cancelled run no longer requires every station to hold a box.
        // Closure still requires every handshake cleared and every lease released.
        try{await BaselineAsync(parameters.Capture().Pickups.Select(p=>p.StationId),requireStationBoxes:false,ct);}
        catch(InvalidOperationException e){throw new FleetClosurePendingException(e.Message);}
        try{await LabAsync(run,"stop",[],ct);}
        catch(FleetLabSessionMissingException)when(run.StopRequested)
        {
            // Only the explicit reviewed-close path can reach this branch: both tasks,
            // every PLC baseline and all leases have already been verified above.
        }
    }
}
