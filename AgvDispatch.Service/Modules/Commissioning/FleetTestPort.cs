using System.Net.Http.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
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
        if(!vehicles.TryGet(id,out var v)||!Fresh(v.LastSuccessfulPollAt)||v.Connection!=ConnectionState.Online||v.Quality!=SignalQuality.Good)
            return id+"通信离线或数据超过3秒未更新。";
        if(v.Blocked!=false||v.EmergencyStopped!=false||v.LocalizationConfidence is not >=.5||
            v.Alarms.Any(a=>a.Severity is AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error or AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Fatal))
            return id+"定位、急停、阻塞或报警状态未就绪。";
        return null;
    }
    private async Task<TaskCenterRow[]> AllAsync(CancellationToken ct)
    {
        var result=new List<TaskCenterRow>();
        for(var page=1;page<=10000;page++)
        {var batch=await tasks.QueryAsync(new(TaskListFilter.All,"",page,100),ct);result.AddRange(batch.Items);if(page*100>=batch.TotalCount)return result.ToArray();}
        throw new InvalidOperationException("任务分页未能完整核对。");
    }
    private void Configuration()
    {
        if(!options.Enabled||options.VehicleIds.Length!=2||options.VehicleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2)
            throw new InvalidOperationException("请使用双车测试启动配置，并明确两辆参与车辆。");
        if(!simulatorOnly||!creation.Enabled||creation.AutomaticCreationEnabled||!dispatch.Enabled||dispatch.MaxConcurrentTasks!=2||
            !handshake.Enabled||handshake.PlcContractVersion!=HandshakeExecutionOptions.Version)
            throw new InvalidOperationException("双车测试须使用本机模拟PLC、两任务并发、当前28阶段契约，且关闭独立自动建单器。");
        if(!gate.CanStartNewWork)throw new InvalidOperationException(gate.NewWorkBlockReason??"启动恢复尚未放行。");
        if(DumperWaitingBaySafety.GetError(map.Current?.Definition,dispatch,true) is {} error)throw new InvalidOperationException(error);
    }
    private void PlcHealthy()
    {
        var rows=signals.GetAll();
        foreach(var station in new[]{"CNC-001","CNC-002",parameters.Capture().DumperStationId})
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
        if(!plan.VehicleIds.Order().SequenceEqual(options.VehicleIds.Order()))throw new InvalidOperationException("参与车辆与本次启动配置不一致。");
        if((await single.ReadAsync(ct)).Current is {Stage:not "Completed"})throw new InvalidOperationException("单车原运行尚未闭合。");
        if((await AllAsync(ct)).Any(t=>t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled)))
            throw new InvalidOperationException("启动前须处理原业务任务。");
        var choices=await manual.GetOptionsAsync(ct);
        if(BusinessRunPort.ConfigurationError(choices,["CNC-001","CNC-002"]) is {} error)throw new InvalidOperationException(error);
        var sites=new HashSet<string>(StringComparer.Ordinal);
        var current=map.Current??throw new InvalidOperationException("运行地图不存在。");
        foreach(var id in plan.VehicleIds)
        {
            if(Health(id) is {} block)throw new InvalidOperationException(block);
            var choice=choices.Vehicles.SingleOrDefault(v=>v.VehicleId==id);
            if(choice is not {Available:true}||!sites.Add(choice.CurrentSite))throw new InvalidOperationException(id+"不可派发或两车共用同一初始停车点："+choice?.Explanation);
            vehicles.TryGet(id,out var state);
            if(state!.VelocityX is not {} vx||state.VelocityY is not {} vy||state.AngularVelocity is not {} va||Math.Abs(vx)>=.01||Math.Abs(vy)>=.01||Math.Abs(va)>=.01||state.Tasks.Any(t=>t.Status is not (4 or 5 or 6 or 7)))
                throw new InvalidOperationException(id+"尚未停稳或仍有车端任务。");
            foreach(var pickup in parameters.Capture().Pickups)
            {
                var check=await preflight.CheckAsync(id,choice.CurrentSite,pickup.PickupSite,dispatch.GetDumperWaitingSite()!,dispatch.DumperDumpingSite,current.MapId,current.VersionId,ct);
                if(!check.Accepted)throw new InvalidOperationException(id+"车端地图/路线核验失败："+check.Message);
            }
        }
        await BaselineAsync(ct);
        if(!ReferenceEquals(current,map.Current))throw new InvalidOperationException("核验期间地图已改变。");
        return(current.MapId,current.VersionId,parameters.Revision);
    }
    public async Task CheckCurrentAsync(FleetTestRun run,CancellationToken ct)
    {
        Configuration();PlcHealthy();
        if(map.Current?.MapId!=run.MapId||map.Current.VersionId!=run.MapVersionId||parameters.Revision!=run.ParameterRevision)
            throw new InvalidOperationException("地图或参数与原测试记录不符。");
        foreach(var id in run.Plan.VehicleIds)if(Health(id) is {} block)throw new InvalidOperationException(block);
        var rows=await AllAsync(ct);
        foreach(var row in rows.Where(t=>t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled)))
        {
            var call=run.Calls.SingleOrDefault(c=>c.StationId==row.StationId&&c.Released);
            if(call==null||call.TaskId!=null&&call.TaskId!=row.TaskId||row.CreatedAt<run.StartedAt||row.RequestedVehicleId!=null)
                throw new InvalidOperationException("存在不属于本次双车测试的任务，保持暂停。");
            if(row.VehicleId!=null&&!run.Plan.VehicleIds.Contains(row.VehicleId))throw new InvalidOperationException("任务分配到了本次范围外车辆。");
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
        {vehicles.TryGet(id,out var v);return new FleetTestVehicle(id,v?.CurrentStation,Health(id)==null,Health(id)??"状态数据有效");}).ToArray(),details.ToArray(),DateTimeOffset.UtcNow);
    }
    public async Task LabAsync(FleetTestRun run,string action,string[] stations,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"fleet")
        {Content=JsonContent.Create(new{runId=run.RunId,action,stations,taskId=action=="bind"?run.Calls.Single(c=>c.StationId==stations.Single()).TaskId:null})};
        request.Headers.Add("X-Agv-Lab-Control","business-run-v1");
        using var response=await lab.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)
        {
            var message=await response.Content.ReadAsStringAsync(ct);
            if(response.StatusCode==System.Net.HttpStatusCode.Conflict&&message.Contains("模拟器原双车会话不存在",StringComparison.Ordinal))
                throw new FleetLabSessionMissingException(message);
            throw new InvalidOperationException("模拟器未接受："+message);
        }
    }
    public async Task<string?> CreateAsync(FleetTestRun run,string station,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(MapExecutionOperation.NewWork,ct);
        if(lease==null)throw new InvalidOperationException("新任务门禁未放行。");
        var originals=(await AllAsync(ct)).Where(t=>t.StationId==station&&t.CreatedAt>=run.StartedAt).ToArray();
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
        if(detail?.Task.StationId!=station)throw new InvalidOperationException("受理应答的原任务与工位不符。");
        if(detail.Task.Stage!=DispatchTaskStage.Created)return;
        await plc.WriteSignalAsync(station,"FullBoxRequestAcknowledged",true,ct);await repository.MarkAcknowledgedAsync(taskId,ct);
    }
    public async Task FeedbackAsync(FleetTestRun run,TaskCenterDetail detail,CancellationToken ct)
    {
        if(!run.Calls.Any(c=>c.TaskId==detail.Task.TaskId&&c.StationId==detail.Task.StationId))throw new InvalidOperationException("原任务身份不符。");
        await LabAsync(run,"bind",[detail.Task.StationId],ct);
        if(detail.Task.Stage==DispatchTaskStage.Created)
        {
            await AcknowledgeAsync(detail.Task.StationId,detail.Task.TaskId,ct);
            return;
        }
        if(detail.Task.VehicleId is not {} id)return;
        if(!run.Plan.VehicleIds.Contains(id))throw new InvalidOperationException("反馈车辆超出本轮范围。");
        var fake=new BusinessRunRecord(run.RunId,new(run.Plan.RequestId,"Cycle",id,[detail.Task.StationId],1,"",true),run.MapId,run.MapVersionId,
            run.ParameterRevision,"双车测试","Executing","",0,detail.Task.TaskId,null,null,run.StopRequested,run.StartedAt);
        await ((BusinessRunPort)business).ApplySimulatorFeedbackAsync(fake,detail,"fleet-feedback",ct);
    }
    private async Task BaselineAsync(CancellationToken ct)
    {
        PlcHealthy();
        foreach(var station in new[]{"CNC-001","CNC-002"})
        {
            foreach(var name in new[]{"FullBoxRequested","FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"})
                if(signals.GetAll().SingleOrDefault(s=>s.StationId==station&&s.VariableName==name)?.Value!=false)throw new InvalidOperationException(station+"旧呼叫/返箱责任未闭合。");
            if(signals.GetAll().SingleOrDefault(s=>s.StationId==station&&s.VariableName=="BoxPresent")?.Value!=true)
                throw new InvalidOperationException(station+"未确认有箱。");
        }
        foreach(var name in new[]{"AgvAtWaitingPosition","AgvAtDumpingPosition","DumperReady","DumpingCompletedMayLeave"})
            if(signals.GetAll().SingleOrDefault(s=>s.StationId==parameters.Capture().DumperStationId&&s.VariableName==name)?.Value!=false)
                throw new InvalidOperationException("共享倒料握手未闭合。");
        if((await traffic.GetResourcesAsync(ct)).Any(r=>r.Leases.Count>0))throw new InvalidOperationException("仍有交通资源持有记录。");
    }
    public async Task CheckClosedAsync(FleetTestRun run,CancellationToken ct)
    {
        await CheckCurrentAsync(run,ct);
        if((await AllAsync(ct)).Any(t=>t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled)))throw new InvalidOperationException("原任务尚未全部闭合。");
        try{await BaselineAsync(ct);}catch(InvalidOperationException e){throw new FleetClosurePendingException(e.Message);}
        try{await LabAsync(run,"stop",[],ct);}
        catch(FleetLabSessionMissingException)when(run.StopRequested)
        {
            // Only the explicit reviewed-close path can reach this branch: both tasks,
            // every PLC baseline and all leases have already been verified above.
        }
    }
}
