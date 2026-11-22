using System.Text.Json;
using System.Text.Json.Nodes;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Traffic;

namespace AgvDispatch.Service;

public static class TaskHandshakeSignals
{
    public static readonly string[] Pickup = ["FullBoxRequested","BoxPresent","Faulted","CommunicationOnline","FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"];
    public static readonly string[] Dumper = ["AgvAtWaitingPosition","AgvAtDumpingPosition","DumperReady","DumpingCompletedMayLeave","Faulted"];
    public static IReadOnlyList<TaskSignalEvidence> Capture(IPlcSignalCache cache,string station,string dumper) =>
        Pickup.Select(v=>Read(cache,station,v)).Concat(Dumper.Select(v=>Read(cache,dumper,v))).ToArray();
    private static TaskSignalEvidence Read(IPlcSignalCache cache,string station,string variable)
    {
        cache.TryGet(station,variable,out var s);
        return new(station,variable,s?.Value,s?.Quality.ToString()??"Unknown",s?.UpdatedAt,s?.Version,s?.Address??"未配置");
    }
}

public sealed partial class PersistentHandshakeExecutor(ITaskHandshakeStore store,IPlcGateway gateway,IPlcSignalCache signals,
    IDispatchOrchestrationRepository tasks,IDispatchTaskRepository taskReader,ITrafficResourceManager traffic,
    DispatchTaskCreationOptions creation,DispatchOrchestratorOptions dispatch,HandshakeExecutionOptions options,
    IAgvStateCache vehicles,IActiveMapRuntime map,IAgvGateway? agvGateway=null,IRobokitTaskMapPreflight? mapPreflight=null,
    DispatchTrafficReservationService? trafficLayer=null, DispatchOptimizationService? optimization=null,
    IRoutePlanner? routePlanner=null)
{
    private readonly DispatchTrafficReservationService _trafficLayer = trafficLayer ?? new(traffic, optimization, tasks, optimization);
    public bool Enabled => options.Enabled;

    public async Task ObserveEquipmentWaitingAsync(DispatchTaskStepWorkItem step, CancellationToken ct)
    {
        try
        {
            if (await EquipmentProposalAsync(step, ct) is { } proposal)
                await _trafficLayer.ObserveAsync(proposal, ct);
            else await SuspendEquipmentRecommendationAsync(step, ct);
        }
        catch
        {
            await SuspendEquipmentRecommendationAsync(step, CancellationToken.None);
            throw;
        }
    }

    private Task SuspendEquipmentRecommendationAsync(DispatchTaskStepWorkItem step, CancellationToken ct) =>
        _trafficLayer.SuspendRecommendationAsync(new(step.StepId, step.TaskId, step.VehicleId ?? "",
            step.SourceSite ?? dispatch.GetDumperWaitingSite() ?? "", dispatch.DumperDumpingSite, [],
            options.DumperExit.ResourceIds, true, "倒料候选条件未成立，撤回未授予推荐；既有设备及实际路权保持。")
            { MapId = map.Current?.MapId, MapVersionId = map.Current?.VersionId, WaitingCategory = "Equipment" }, ct);

    private async Task<TrafficRouteObservation?> EquipmentProposalAsync(DispatchTaskStepWorkItem step, CancellationToken ct)
    {
        if (!Enabled || options.PlcContractVersion != HandshakeExecutionOptions.Version ||
            step.StepType != DispatchTaskStepType.WaitForDumperReady || string.IsNullOrWhiteSpace(step.VehicleId) ||
            map.Current is not { } current || !Is(creation.DumperStationId, "Faulted", false)) return null;
        var root = (await taskReader.GetStepsAsync(step.TaskId, ct)).FirstOrDefault(s => s.StepType == DispatchTaskStepType.GoToPickup);
        var binding = root is null ? null : await store.GetAsync(step.TaskId, root.StepId, ct);
        if (binding?.ContractVersion != HandshakeExecutionOptions.Version || !BindingMatches(binding, step)) return null;
        var waiting = await WaitingSiteAsync(step, ct);
        IReadOnlyList<string> admissionResources = options.DumperExit.ResourceIds;
        if (routePlanner is not null)
        {
            admissionResources = DumperWaitingBaySafety.EquipmentEntryResources(
                routePlanner, waiting, dispatch.DumperDumpingSite, admissionResources)
                ?? throw new InvalidOperationException("候车位到倒料位的完整入口路径不可用，不取得单独的设备资源。");
        }
        if (!await store.OwnsEquipmentAsync(step.TaskId, creation.DumperStationId, ct))
        {
            if (!vehicles.TryGet(step.VehicleId, out var vehicle) || vehicle.Connection != ConnectionState.Online ||
                vehicle.Quality != SignalQuality.Good || vehicle.EmergencyStopped != false || vehicle.Blocked != false ||
                AgvObstaclePolicy.IsBlocked(vehicle) || AgvObstaclePolicy.HasBlockingAlarm(vehicle) ||
                vehicle.Tasks.Any(t => t.Status is 1 or 2 or 3) ||
                vehicle.LastSuccessfulPollAt is not { } observed || observed > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(1) ||
                AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow() - observed > TimeSpan.FromSeconds(Math.Clamp(options.StateMaxAgeSeconds, 1, 60)) ||
                !current.Definition.Nodes.Any(n => n.Enabled &&
                    MapRouteSiteMapper.IsSameNode(current.Definition, n.Id, waiting) &&
                    MapRouteSiteMapper.IsSameNode(current.Definition,
                        n.Id, vehicle.CurrentStation))) return null;
        }
        return new(step.StepId, step.TaskId, step.VehicleId, waiting, dispatch.DumperDumpingSite,
            [], admissionResources, true, "倒料设备与完整入口路径按原任务有效优先级原子排队；已有归属保持不变。")
            { MapId = current.MapId, MapVersionId = current.VersionId, WaitingCategory = "Equipment" };
    }
    public async Task BindBeforeTransportAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        if(!Enabled)
        {
            if(await store.GetAsync(step.TaskId,step.StepId,ct)!=null)throw new InvalidOperationException("原任务绑定新握手契约，执行未启用时禁止降级准备运输。");
            return;
        }
        if(options.PlcContractVersion!=HandshakeExecutionOptions.Version)throw new InvalidOperationException("新握手PLC契约版本未匹配，不准备首段运输。");
        if(map.Current==null)throw new InvalidOperationException("新契约缺少运行地图。");
        if(DumperWaitingBaySafety.GetError(map.Current.Definition,dispatch,true) is {} bayError)throw new InvalidOperationException(bayError);
        ValidateExitConfiguration(step);
        var observed=TaskHandshakeSignals.Capture(signals,step.StationId,creation.DumperStationId);
        foreach(var signal in observed)
        {
            signals.TryGet(signal.StationId,signal.VariableName,out var sample);
            if(sample?.Value is not bool value||!TaskSignalPredicate.EvaluateFresh(signal.StationId,signal.VariableName,value,sample,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),options.StateMaxAgeSeconds).Satisfied)
                throw new InvalidOperationException("首段运输前13个契约信号必须新鲜有效："+signal.StationId+"/"+signal.VariableName);
        }
        if(!Is(step.StationId,"Faulted",false)||!Is(step.StationId,"CommunicationOnline",true)||!Is(creation.DumperStationId,"Faulted",false))throw new InvalidOperationException("设备故障或通讯异常，不准备新契约首段运输。");
        if(!Is(step.StationId,"FullBoxRequested",false)||!Is(step.StationId,"FullBoxRequestAcknowledged",true))throw new InvalidOperationException("28阶段需先受理应答1、PLC撤呼叫0，再开始取箱运输。");
        if(!Is(step.StationId,"BoxPresent",true)||new[]{"FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.Any(v=>!Is(step.StationId,v,false)))
            throw new InvalidOperationException("28阶段首段取箱前需原工位有箱，且取箱通知、返箱责任、放箱通知均为0；保留原命令，禁止带入未闭合周期。");
        var existing = await store.GetAsync(step.TaskId, step.StepId, ct);
        if (existing is not null && existing.ContractVersion != HandshakeExecutionOptions.Version)
            throw new InvalidOperationException("原任务已绑定旧PLC契约，禁止静默迁移为28阶段或删除原检查点。");
        if (existing is not null && !BindingMatches(existing, step) &&
            !await store.RollbackUncommittedAsync(step.TaskId, step.StepId, ct))
            throw new InvalidOperationException("首段原契约绑定与当前配置不一致。");
        var baseline=observed.Concat(new[]{new TaskSignalEvidence("__context","Binding",null,"Context",AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),null,Binding(step))}).ToArray();
        var bound=await store.BeginAsync(step,HandshakeExecutionOptions.Version,"TransportBound",baseline,ct);
        if(!BindingMatches(bound,step))throw new InvalidOperationException("首段原契约绑定与当前配置不一致。");
        foreach(var original in bound.Baseline.Where(b=>b.StationId!="__context"))
            if(!signals.TryGet(original.StationId,original.VariableName,out var current)||current.Address!=original.Address)
                throw new InvalidOperationException("首段原点表地址已变化，保留原绑定和命令，禁止静默改用新地址发车。");
    }

    public async Task ValidateTransportAsync(DispatchTaskStepWorkItem step,CancellationToken ct,
        bool dumperExitHandoverComplete=false)
    {
        if(!Enabled)return;
        if(options.PlcContractVersion!=HandshakeExecutionOptions.Version)throw new InvalidOperationException("当前不是28阶段契约，禁止准备/发送运输。");
        if(step.StepType is DispatchTaskStepType.DeliverFullBoxToDumper or DispatchTaskStepType.EnterDumpingPosition &&
            DumperWaitingBaySafety.GetError(map.Current?.Definition,dispatch,true) is {} bayError)throw new InvalidOperationException(bayError);
        var priorType=step.StepType switch {DispatchTaskStepType.DeliverFullBoxToDumper=>DispatchTaskStepType.PickupFullBox,DispatchTaskStepType.EnterDumpingPosition=>DispatchTaskStepType.WaitForDumperReady,DispatchTaskStepType.ReturnEmptyBox=>DispatchTaskStepType.DumpFullBox,_=>throw new InvalidOperationException("不支持的运输阶段。")};
        var prior=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==priorType);
        var checkpoint=prior==null?null:await store.GetAsync(step.TaskId,prior.StepId,ct);
        if(checkpoint?.Phase!="ReadyToComplete"||checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.PendingSignal!=null||!BindingMatches(checkpoint,step))
            throw new InvalidOperationException("前序持久化握手或契约绑定尚未核对，禁止准备新运输。");
        if(!Is(creation.DumperStationId,"Faulted",false))throw new InvalidOperationException("倒料机故障或反馈无效，禁止准备后续运输。");
        if(!Is(step.StationId,"Faulted",false)||!Is(step.StationId,"CommunicationOnline",true)||!Is(step.StationId,"FullBoxRemoved",true)||!Is(step.StationId,"EmptyBoxTaskAcknowledged",true))throw new InvalidOperationException("原工位故障/通讯或取箱、返箱保持责任不成立，禁止后续运输。");
        // ReturnEmptyBox is validated before the first leaving window and again for
        // each later rolling-window command. Once the persisted exit handover has
        // transferred the dumper to the next task, later windows must no longer
        // require this task to keep equipment ownership or at-position outputs.
        if(step.StepType is DispatchTaskStepType.EnterDumpingPosition or DispatchTaskStepType.ReturnEmptyBox &&
            !(step.StepType==DispatchTaskStepType.ReturnEmptyBox&&dumperExitHandoverComplete))
            if(!await store.OwnsEquipmentAsync(step.TaskId,creation.DumperStationId,ct))throw new InvalidOperationException("原任务倒料设备归属缺失，禁止进入或离开指令准备/发送。");
        if(step.StepType==DispatchTaskStepType.ReturnEmptyBox&&!dumperExitHandoverComplete&&
            (!Is(creation.DumperStationId,"DumpingCompletedMayLeave",true)||!Is(creation.DumperStationId,"AgvAtDumpingPosition",true)))
            throw new InvalidOperationException("本轮离开许可或倒料位保持未成立，禁止准备返程。");
    }

    public async Task ValidateRecoveryTransportAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        if(!Enabled)return;
        if(options.PlcContractVersion!=HandshakeExecutionOptions.Version)
            throw new InvalidOperationException("当前不是28阶段契约，禁止发送恢复运输。");
        if(step.StepType!=DispatchTaskStepType.GoToPickup)
        {
            await ValidateTransportAsync(step,ct);
            return;
        }
        if(!Is(step.StationId,"Faulted",false)||!Is(step.StationId,"CommunicationOnline",true)||
            !Is(creation.DumperStationId,"Faulted",false))
            throw new InvalidOperationException("恢复首段前工位或倒料机故障/通讯条件不成立。");
        if(!Is(step.StationId,"FullBoxRequested",false)||!Is(step.StationId,"FullBoxRequestAcknowledged",true)||
            !Is(step.StationId,"BoxPresent",true)||
            new[]{"FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.Any(v=>!Is(step.StationId,v,false)))
            throw new InvalidOperationException("恢复首段前工位箱体、呼叫应答或PLC周期尚未闭合。");
        var root=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.GoToPickup);
        var binding=root==null?null:await store.GetAsync(step.TaskId,root.StepId,ct);
        if(binding?.ContractVersion!=HandshakeExecutionOptions.Version||!BindingMatches(binding,step))
            throw new InvalidOperationException("恢复首段缺少原任务28阶段绑定，禁止直接发送替代导航。");
    }

    public async Task ValidatePendingTransportAsync(string taskId,string stepId,string vehicleId,
        bool continuationWindow,CancellationToken ct)
    {
        var task=(await tasks.GetAssignedTasksAsync(ct)).Concat(await tasks.GetSubmittingTasksAsync(ct)).SingleOrDefault(t=>t.TaskId==taskId)
            ??throw new InvalidOperationException("原任务已冻结或不在可发送阶段。");
        var original=(await taskReader.GetStepsAsync(taskId,ct)).Single(s=>s.StepId==stepId);
        var validStatus = continuationWindow
            ? original.Status is DispatchTaskStepStatus.Executing or DispatchTaskStepStatus.CommandPending or
                DispatchTaskStepStatus.Sending or DispatchTaskStepStatus.RetryScheduled
            : original.Status is DispatchTaskStepStatus.CommandPending or DispatchTaskStepStatus.RetryScheduled;
        if(task.VehicleId!=vehicleId||original.VehicleId!=vehicleId||!validStatus)throw new InvalidOperationException("待发命令车辆或原步骤状态不一致。");
        var step=new DispatchTaskStepWorkItem(taskId,task.StationId,vehicleId,task.Stage,stepId,original.Sequence,original.StepType,original.Status,original.SourceSite,original.DestinationSite,original.UpdatedAt);
        if(dispatch.GetDumperWaitingSites().Count>1 && original.StepType is DispatchTaskStepType.DeliverFullBoxToDumper or DispatchTaskStepType.EnterDumpingPosition)
        {
            var waiting=await WaitingSiteAsync(step,ct);
            var recorded=original.StepType==DispatchTaskStepType.DeliverFullBoxToDumper ? original.DestinationSite : original.SourceSite;
            // Command paths may carry vendor site ids (LMxx) while the waiting-bay
            // binding stores the logical BUS-* node; compare both against the map.
            if(!string.IsNullOrWhiteSpace(recorded) && map.Current?.Definition is { } waitingDefinition)
            {
                recorded=MapRouteSiteMapper.ResolveLogicalNodeId(waitingDefinition,recorded);
                waiting=MapRouteSiteMapper.ResolveLogicalNodeId(waitingDefinition,waiting);
            }
            if(!string.Equals(recorded,waiting,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("待发命令与原任务持久候车位不一致，禁止发送。");
        }
        if(original.StepType==DispatchTaskStepType.GoToPickup)await BindBeforeTransportAsync(step,ct);
        else
        {
            var checkpoint=await store.GetAsync(taskId,stepId,ct);
            var dumperExitHandoverComplete=continuationWindow&&
                original.StepType==DispatchTaskStepType.ReturnEmptyBox&&
                checkpoint?.ContractVersion==HandshakeExecutionOptions.Version&&
                checkpoint.Phase=="ExitHandoverComplete";
            await ValidateTransportAsync(step,ct,dumperExitHandoverComplete);
        }
    }
    private bool Is(string station,string variable,bool expected)
    {
        signals.TryGet(station,variable,out var s);
        return TaskSignalPredicate.EvaluateFresh(station,variable,expected,s,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),options.StateMaxAgeSeconds).Satisfied;
    }

    public async Task<bool> ProcessAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        if(!Enabled)
        {
            var first=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.GoToPickup);
            if(first!=null&&await store.GetAsync(step.TaskId,first.StepId,ct)!=null)throw new InvalidOperationException("原任务已绑定新握手，当前执行关闭，禁止降级旧执行器。");
            return false;
        }
        if(step.StepType is not (DispatchTaskStepType.PickupFullBox or DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.DumpFullBox or DispatchTaskStepType.PlaceEmptyBox or DispatchTaskStepType.CompletePlcHandshake))return false;
        if(options.PlcContractVersion!=HandshakeExecutionOptions.Version)throw new InvalidOperationException("新握手契约未配置匹配版本，保持等待；禁止借用旧PLC时序。");
        var root=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.GoToPickup);
        var binding=root==null?null:await store.GetAsync(step.TaskId,root.StepId,ct);
        if(binding?.ContractVersion!=HandshakeExecutionOptions.Version||!BindingMatches(binding,step))
            throw new InvalidOperationException("缺少本任务首段原契约绑定；旧任务禁止自动迁入新握手。");
        var checkpoint=await store.GetAsync(step.TaskId,step.StepId,ct);
        // These proofs gate creation of a new placement/closure checkpoint. Once
        // the durable checkpoint exists, a restart must continue the audited phase
        // instead of requiring a completed return command to remain observable.
        if(step.StepType==DispatchTaskStepType.PlaceEmptyBox&&checkpoint==null&&
            (!await HandoverVerifiedAsync(step.TaskId,ct)||!await ReturnVerifiedAsync(step,ct)))return true;
        // Acquire ownership before interpreting shared signals as this task's initial baseline.
        // A waiting vehicle must never mark another owner's high notification as its own orphan cycle.
        if(step.StepType==DispatchTaskStepType.WaitForDumperReady)
        {
            if(!Is(creation.DumperStationId,"Faulted",false))throw new InvalidOperationException("倒料设备故障或反馈无效，保持等待。");
            var proposal = await EquipmentProposalAsync(step, ct);
            if (proposal is null) { await SuspendEquipmentRecommendationAsync(step, ct); return true; }
            // Equipment and the dumping conflict zone use the same priority queue as transport.
            // Merely polling the shared PLC first no longer wins ownership.
            if (!(await _trafficLayer.AcquireAsync(proposal with { Waiting = false }, ct)).Acquired) return true;
            string? owner;
            try { owner = await store.TryClaimEquipmentAsync(step.TaskId, step.StepId, creation.DumperStationId, ct); }
            catch
            {
                // A store call can fail after committing. Reconcile its actual owner before
                // releasing only this uncommitted step's new route; uncertainty retains it.
                if (!await store.OwnsEquipmentAsync(step.TaskId, creation.DumperStationId, CancellationToken.None))
                    await _trafficLayer.RollbackUncommittedAsync(step.StepId, CancellationToken.None);
                throw;
            }
            var reason=owner!=null ? "倒料机由任务"+owner+"持有，当前任务不消费共享许可。" : "本任务已取得倒料设备归属；PLC就绪握手及实际入站另行核验。";
            if (owner is not null)
            {
                await _trafficLayer.RollbackUncommittedAsync(step.StepId, ct);
                await _trafficLayer.ObserveAsync(proposal with { Reason = reason }, ct);
            }
            // Another task's valid claim is normal queueing. The recorded traffic
            // wait identifies its owner; do not turn it into a failed step or
            // interpret the owner's shared PLC signals as this task's baseline.
            if(owner!=null)return true;
        }
        if(step.StepType==DispatchTaskStepType.DumpFullBox&&!await store.OwnsEquipmentAsync(step.TaskId,creation.DumperStationId,ct))throw new InvalidOperationException("本轮倒料设备归属缺失，禁止重新认领并开启周期。");
        if(checkpoint==null)
        {
            // Any prior notification on first encounter is ambiguous: do not reset/reissue it.
            var observed=TaskHandshakeSignals.Capture(signals,step.StationId,creation.DumperStationId)
                .Where(s=>step.StepType is not (DispatchTaskStepType.PlaceEmptyBox or DispatchTaskStepType.CompletePlcHandshake)||s.StationId==step.StationId).ToArray();
            if(observed.Any(s=>s.Value==null||s.Quality!="Good"||s.ObservedAt==null||AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow()-s.ObservedAt>TimeSpan.FromSeconds(Math.Clamp(options.StateMaxAgeSeconds,1,60))||s.ObservedAt>AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(1)))
                throw new InvalidOperationException("新契约初始基线需要13个新鲜有效信号；当前缺失/过期，未创建可执行阶段。");
            var baseline=observed.Concat(new[]{new TaskSignalEvidence("__context","Binding",null,"Context",AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),null,Binding(step))}).ToArray();
            var initial=Initial(step.StepType);
            var orphan=step.StepType switch
            {
                DispatchTaskStepType.PickupFullBox => Is(step.StationId,"FullBoxRemoved",true)||Is(step.StationId,"EmptyBoxTaskAcknowledged",true),
                DispatchTaskStepType.WaitForDumperReady => Is(creation.DumperStationId,"AgvAtWaitingPosition",true),
                DispatchTaskStepType.DumpFullBox => Is(creation.DumperStationId,"AgvAtDumpingPosition",true),
                DispatchTaskStepType.PlaceEmptyBox => Is(step.StationId,"EmptyBoxPlaced",true),
                _=>false
            };
            checkpoint=await store.BeginAsync(step,HandshakeExecutionOptions.Version,orphan?"ReviewRequired":initial,baseline,ct);
        }
        if(checkpoint is null)
            throw new InvalidOperationException("握手检查点创建失败，禁止继续执行当前步骤。");
        if(checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.PendingSignal!=null||checkpoint.Phase=="ReviewRequired")
            throw new InvalidOperationException("原握手存在版本冲突、写入结果未知或缺少原始阶段；保留原周期，进入恢复核对，禁止自动重发。");
        if(!BindingMatches(checkpoint,step))
            throw new InvalidOperationException("地图、站点映射或动作参数与原握手绑定不一致，禁止静默迁移。");
        foreach(var original in checkpoint.Baseline.Where(b=>b.StationId!="__context"))
            if(!signals.TryGet(original.StationId,original.VariableName,out var current)||current.Address!=original.Address)
                throw new InvalidOperationException("点表映射与原始周期不一致，禁止自动改用新地址。");
        var station=step.StationId;var dumper=creation.DumperStationId;
        if(step.StepType is DispatchTaskStepType.PickupFullBox or DispatchTaskStepType.PlaceEmptyBox or DispatchTaskStepType.CompletePlcHandshake)
            if(!Is(station,"Faulted",false)||!Is(station,"CommunicationOnline",true))throw new InvalidOperationException("来源工位故障、通讯异常或数据无效，保持握手阶段。");
        if(step.StepType is DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.DumpFullBox)
            if(!Is(dumper,"Faulted",false))throw new InvalidOperationException("倒料机故障或故障反馈无效，保持握手阶段。");
        Task Move(string phase,string reason)=>MoveAsync(checkpoint,phase,reason,ct);
        Task Write(string device,string signal,bool value,string next)=>WriteAsync(checkpoint,device,signal,value,next,ct);
        if(step.StepType==DispatchTaskStepType.CompletePlcHandshake&&checkpoint==null&&
            (!await ReturnVerifiedAsync(step,ct)||!await HandoverVerifiedAsync(step.TaskId,ct)||!Is(station,"BoxPresent",true)||!Is(station,"FullBoxRequested",false)))return true;
        switch(checkpoint!.Phase)
        {
            case "AwaitBoxAbsent":
                if(Is(station,"BoxPresent",false))await Move("NotifyRemoved","观察本轮取箱步骤箱体不存在；不替代车端取货终态。");break;
            case "NotifyRemoved":await Write(station,"FullBoxRemoved",true,"NotifyEmptyResponsibility");break;
            case "NotifyEmptyResponsibility":await Write(station,"EmptyBoxTaskAcknowledged",true,"AwaitPickupReadback");break;
            case "AwaitPickupReadback":
                if(Is(station,"FullBoxRequested",false)&&Is(station,"BoxPresent",false)&&Is(station,"FullBoxRemoved",true)&&Is(station,"EmptyBoxTaskAcknowledged",true))await Move("ClearCallResponse","呼叫已撤销且输出回读成立；回读不等于PLC业务ACK。");break;
            case "ClearCallResponse":if(Is(station,"FullBoxRequested",false))await Write(station,"FullBoxRequestAcknowledged",false,"ReadyToComplete");break;
            case "AwaitReadyBaseline":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"AgvAtDumpingPosition",false)&&Is(dumper,"DumperReady",false)&&Is(dumper,"DumpingCompletedMayLeave",false))await Move("NotifyWaiting","28阶段新周期基线：等待、倒料周期、Ready、倒料完成反馈均为0。");break;
            case "NotifyWaiting":if(Is(dumper,"Faulted",false))await Write(dumper,"AgvAtWaitingPosition",true,"AwaitReady");break;
            case "AwaitReady":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"AgvAtWaitingPosition",true)&&Is(dumper,"DumperReady",true))await Move("ConsumeReady","本任务观察到入站许可；下一阶段撤销等待通知供PLC消费。");break;
            case "ConsumeReady":if(Is(dumper,"Faulted",false))await Write(dumper,"AgvAtWaitingPosition",false,"AwaitReadyConsumed");break;
            case "AwaitReadyConsumed":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"DumperReady",false))await Move("ReadyToComplete","PLC按新契约撤销Ready；保留本轮已消费记录，不要求Ready持续为1。");break;
            case "AwaitDoneBaseline":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"AgvAtDumpingPosition",false)&&Is(dumper,"DumpingCompletedMayLeave",false))await Move("NotifyDumping","已在本轮通知之前观察到Done=0；未把历史高电平作为完成。");break;
            case "NotifyDumping":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"DumpingCompletedMayLeave",false))await Write(dumper,"AgvAtDumpingPosition",true,"AwaitCurrentDone");break;
            case "AwaitCurrentDone":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"AgvAtDumpingPosition",true)&&Is(dumper,"DumpingCompletedMayLeave",true)&&
                    signals.TryGet(dumper,"DumpingCompletedMayLeave",out var done)&&done.UpdatedAt>checkpoint.UpdatedAt)
                    await Move("ReadyToComplete","本轮到位通知后观察Done=1；离站尚未确认，D2继续保持，不释放占用。");break;
            case "NotifyPlaced":await Write(station,"EmptyBoxPlaced",true,"AwaitBoxPresent");break;
            case "AwaitBoxPresent":if(Is(station,"EmptyBoxPlaced",true)&&Is(station,"BoxPresent",true))await Move("ReadyToComplete","本轮放箱步骤观察到箱体在位；PLC业务接收仍按契约解释。");break;
            case "AwaitReturnEvidence":
                if(await ReturnVerifiedAsync(step,ct)&&await HandoverVerifiedAsync(step.TaskId,ct)&&Is(station,"BoxPresent",true))await Move("ClearRemoved","已返原工位且本轮出区交接完成；开始28阶段来源工位收尾。");break;
            case "ClearRemoved":await Write(station,"FullBoxRemoved",false,"ClearPlaced");break;
            case "ClearPlaced":if(Is(station,"FullBoxRemoved",false))await Write(station,"EmptyBoxPlaced",false,"ClearEmptyResponsibility");break;
            case "ClearEmptyResponsibility":if(Is(station,"FullBoxRemoved",false)&&Is(station,"EmptyBoxPlaced",false))await Write(station,"EmptyBoxTaskAcknowledged",false,"VerifyClosure");break;
            case "VerifyClosure":
                if(Is(station,"BoxPresent",true)&&new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.All(v=>Is(station,v,false))&&
                    await HandoverVerifiedAsync(step.TaskId,ct)&&await ReturnVerifiedAsync(step,ct))await Move("ReadyToComplete","28阶段来源工位闭合，返箱责任最后解除；共享设备可已由后车使用。");break;
            case "ReadyToComplete":
                var valid=step.StepType switch
                {
                    DispatchTaskStepType.PickupFullBox=>Is(station,"FullBoxRequested",false)&&Is(station,"BoxPresent",false)&&Is(station,"FullBoxRemoved",true)&&Is(station,"EmptyBoxTaskAcknowledged",true)&&Is(station,"FullBoxRequestAcknowledged",false),
                    DispatchTaskStepType.WaitForDumperReady=>Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"DumperReady",false),
                    DispatchTaskStepType.DumpFullBox=>Is(dumper,"AgvAtDumpingPosition",true)&&Is(dumper,"DumpingCompletedMayLeave",true),
                    DispatchTaskStepType.PlaceEmptyBox=>Is(station,"BoxPresent",true)&&Is(station,"EmptyBoxPlaced",true),
                    DispatchTaskStepType.CompletePlcHandshake=>Is(station,"BoxPresent",true)&&new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.All(v=>Is(station,v,false))&&await HandoverVerifiedAsync(step.TaskId,ct)&&await ReturnVerifiedAsync(step,ct),
                    _=>false
                };
                if(valid&&await tasks.CompleteReadyStepAsync(step.TaskId,step.StepId,ct)&&step.StepType==DispatchTaskStepType.CompletePlcHandshake)
                {await traffic.ReleaseTaskAsync(step.TaskId,ct);}
                break;
            default:throw new InvalidOperationException("未知持久化握手阶段，禁止回退或重跑："+checkpoint.Phase);
        }
        return true;
    }

    private async Task<string> WaitingSiteAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        if(dispatch.GetDumperWaitingSites().Count<=1)return dispatch.GetDumperWaitingSite()??"";
        var selected=traffic is ITaskWaitingBayAllocator allocator ? await allocator.GetAsync(step.TaskId,ct) : null;
        if(selected is null || selected.VehicleId!=step.VehicleId || selected.MapId!=map.Current?.MapId ||
            selected.MapVersionId!=map.Current?.VersionId || map.Current is null ||
            !DumperWaitingBaySafety.IsPhysicalWaitingSite(map.Current.Definition,selected.WaitingSite,dispatch.DumperDumpingSite))
            throw new InvalidOperationException("原任务持久候车位缺失或绑定不一致，不能用默认点继续握手。");
        return selected.WaitingSite;
    }
    private string Binding(DispatchTaskStepWorkItem step)
    {
        var binding=JsonSerializer.Serialize(new {map.Current?.MapId,map.Current?.VersionId,MapHash=map.Current==null?null:EngineeringRuntimeIdentity.MapHash(map.Current.Definition),Vehicle=step.VehicleId,Pickup=dispatch.GetPickupSite(step.StationId),Waiting=dispatch.GetDumperWaitingSite(),Dump=dispatch.DumperDumpingSite,Dumper=creation.DumperStationId,Exit=options.DumperExit,Actions=dispatch.StepActions.OrderBy(a=>a.Key).ToArray()});
        if(dispatch.GetDumperWaitingSites().Count<=1)return binding;
        var node=JsonNode.Parse(binding)!.AsObject();
        node["WaitingSites"]=JsonSerializer.SerializeToNode(dispatch.GetDumperWaitingSites());
        return node.ToJsonString();
    }
    private bool BindingMatches(HandshakeCheckpoint checkpoint,DispatchTaskStepWorkItem step)
    {
        var recorded=checkpoint.Baseline.FirstOrDefault(s=>s.StationId=="__context")?.Address;
        var current=Binding(step);
        if(recorded==current)return true;
        if(recorded==null)return false;
        try
        {
            var oldRoot=JsonNode.Parse(recorded)?.AsObject();
            var currentRoot=JsonNode.Parse(current)?.AsObject();
            var oldExit=oldRoot?["Exit"] as JsonObject;
            var currentExit=currentRoot?["Exit"] as JsonObject;
            if(oldRoot==null||currentRoot==null||oldExit==null||currentExit==null)return false;
            var canonicalMapHash=map.Current is null?null:EngineeringRuntimeIdentity.MapHash(map.Current.Definition);
            var legacyMapHash=map.Current is null?null:TransportRouteSnapshots.Hash(JsonSerializer.Serialize(map.Current.Definition,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var oldMapHash=oldRoot["MapHash"]?.GetValue<string>();
            var currentMapHash=currentRoot["MapHash"]?.GetValue<string>();
            // In-flight tasks created before this fix recorded the raw enum-number
            // serialization. Accept it only when it exactly matches the current map's
            // legacy representation; every new binding and evidence uses the published
            // canonical hash shared by route snapshots and the Web engineering plan.
            if(canonicalMapHash is null||currentMapHash!=canonicalMapHash||
                oldMapHash!=canonicalMapHash&&oldMapHash!=legacyMapHash)return false;
            oldRoot["MapHash"]=canonicalMapHash;
            currentRoot["MapHash"]=canonicalMapHash;
            var oldTop=oldExit["MinimumConfidence"]?.GetValue<double>();
            var currentTop=currentExit["MinimumConfidence"]?.GetValue<double>();
            var oldPolicy=oldExit["Policy"] as JsonObject;
            var currentPolicy=currentExit["Policy"] as JsonObject;
            var oldPolicyValue=oldPolicy?["MinimumConfidence"]?.GetValue<double>();
            var currentPolicyValue=currentPolicy?["MinimumConfidence"]?.GetValue<double>();
            var oldDeviation=oldExit["MaximumRouteDeviationMeters"]?.GetValue<double>();
            var currentDeviation=currentExit["MaximumRouteDeviationMeters"]?.GetValue<double>();
            if(oldTop is null||currentTop is null||oldPolicyValue is null||currentPolicyValue is null||
                oldDeviation is null||currentDeviation is null)return false;
            // A reviewed test-threshold change may alter only the confidence limit
            // and the route-deviation allowance.
            // Map identity, vehicle, routes, resources, body profile and all other
            // handshake parameters must remain byte-for-byte equivalent.
            if(oldTop<currentTop||oldPolicyValue<currentPolicyValue||oldDeviation<currentDeviation)return false;
            oldExit["MinimumConfidence"]=0;
            currentExit["MinimumConfidence"]=0;
            oldPolicy!["MinimumConfidence"]=0;
            currentPolicy!["MinimumConfidence"]=0;
            oldExit["MaximumRouteDeviationMeters"]=0;
            currentExit["MaximumRouteDeviationMeters"]=0;
            return JsonNode.DeepEquals(oldRoot,currentRoot);
        }
        catch(Exception exception)when(exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private async Task<bool> HandoverVerifiedAsync(string taskId,CancellationToken ct)
    {
        var returned=(await taskReader.GetStepsAsync(taskId,ct)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
        var proof=returned==null?null:await store.GetAsync(taskId,returned.StepId,ct);
        return proof?.ContractVersion==HandshakeExecutionOptions.Version&&proof.Phase=="ExitHandoverComplete"&&proof.PendingSignal==null;
    }

    private async Task<bool> ReturnVerifiedAsync(DispatchTaskStepWorkItem step,CancellationToken ct)=>await ReturnEvidenceAsync(step,ct)!=null;
    private async Task<TaskExitEvidence?> ReturnEvidenceAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        var returned=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
        // PlaceEmptyBox and CompletePlcHandshake are station-level business steps
        // and do not carry VehicleId. The owning vehicle is recorded on the
        // original ReturnEmptyBox step, so use that identity for return evidence.
        if(returned?.Status!=DispatchTaskStepStatus.Completed||string.IsNullOrWhiteSpace(returned.VendorTaskId)||
            string.IsNullOrWhiteSpace(returned.VehicleId)||
            !vehicles.TryGet(returned.VehicleId,out var vehicle)||vehicle.Connection!=ConnectionState.Online||vehicle.Quality!=SignalQuality.Good||vehicle.LastSuccessfulPollAt is not {} time||
            AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow()-time>TimeSpan.FromSeconds(Math.Clamp(options.StateMaxAgeSeconds,1,60))||time>AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(1))return null;
        var pickup=dispatch.GetPickupSite(step.StationId);
        var definition=map.Current?.Definition;
        if(definition==null||string.IsNullOrWhiteSpace(pickup))return null;
        var pickupNodes=definition.Nodes.Where(n=>n.Enabled&&(string.Equals(n.Id,pickup,StringComparison.OrdinalIgnoreCase)||string.Equals(n.AgvSiteId,pickup,StringComparison.OrdinalIgnoreCase))).ToArray();
        var dumpingNodes=definition.Nodes.Where(n=>n.Enabled&&(string.Equals(n.Id,dispatch.DumperDumpingSite,StringComparison.OrdinalIgnoreCase)||string.Equals(n.AgvSiteId,dispatch.DumperDumpingSite,StringComparison.OrdinalIgnoreCase))).ToArray();
        if(pickupNodes.Length!=1||dumpingNodes.Length!=1||string.Equals(pickupNodes[0].Id,dumpingNodes[0].Id,StringComparison.OrdinalIgnoreCase))return null;
        var node=pickupNodes[0];
        var vendorSite=string.IsNullOrWhiteSpace(node.AgvSiteId)?node.Id:node.AgvSiteId;
        if(definition.Nodes.Count(n=>n.Enabled&&string.Equals(string.IsNullOrWhiteSpace(n.AgvSiteId)?n.Id:n.AgvSiteId,vendorSite,StringComparison.OrdinalIgnoreCase))!=1||
            !string.Equals(vehicle.CurrentStation,vendorSite,StringComparison.OrdinalIgnoreCase)||vehicle.EmergencyStopped!=false||vehicle.Blocked!=false||
            AgvObstaclePolicy.IsBlocked(vehicle)||AgvObstaclePolicy.HasBlockingAlarm(vehicle))return null;
        return new(step.TaskId,step.StepId,returned.VehicleId,returned.StepId,returned.VendorTaskId!,vendorSite,node.Id,map.Current!.MapId,map.Current.VersionId,time,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow());
    }

    private async Task WriteAsync(HandshakeCheckpoint checkpoint,string station,string signal,bool value,string next,CancellationToken ct)
    {
        await store.RecordSignalsAsync(checkpoint.TaskId,checkpoint.StepId,TaskHandshakeSignals.Capture(signals,checkpoint.Baseline[0].StationId,creation.DumperStationId),ct);
        var pending=station+"/"+signal;
        if(!await store.MoveAsync(checkpoint,"WritePending","外部写入前已持久化；结果未知时禁止自动重发。",pending,value,next,ct))return;
        // A failure or crash leaves WritePending intact for explicit recovery review.
        await gateway.WriteSignalAsync(station,signal,value,ct);
        var recorded=(await store.GetAsync(checkpoint.TaskId,checkpoint.StepId,ct))!;
        await store.MoveAsync(recorded,next,"写调用成功；不等于PLC业务接收。",null,null,null,ct);
    }
    private async Task MoveAsync(HandshakeCheckpoint checkpoint,string phase,string reason,CancellationToken ct)
    {
        await store.RecordSignalsAsync(checkpoint.TaskId,checkpoint.StepId,TaskHandshakeSignals.Capture(signals,checkpoint.Baseline[0].StationId,creation.DumperStationId),ct);
        _=await store.MoveAsync(checkpoint,phase,reason,null,null,null,ct);
    }
    private static string Initial(DispatchTaskStepType type)=>type switch
    {
        DispatchTaskStepType.PickupFullBox=>"AwaitBoxAbsent",DispatchTaskStepType.WaitForDumperReady=>"AwaitReadyBaseline",
        DispatchTaskStepType.DumpFullBox=>"AwaitDoneBaseline",DispatchTaskStepType.PlaceEmptyBox=>"NotifyPlaced",
        DispatchTaskStepType.CompletePlcHandshake=>"AwaitReturnEvidence",_=>throw new ArgumentOutOfRangeException(nameof(type))
    };
}
