using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

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

public sealed class PersistentHandshakeExecutor(ITaskHandshakeStore store,IPlcGateway gateway,IPlcSignalCache signals,
    IDispatchOrchestrationRepository tasks,IDispatchTaskRepository taskReader,ITrafficResourceManager traffic,
    DispatchTaskCreationOptions creation,DispatchOrchestratorOptions dispatch,HandshakeExecutionOptions options,
    IAgvStateCache vehicles,IActiveMapRuntime map)
{
    public bool Enabled => options.Enabled;
    public async Task BindBeforeTransportAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        if(!Enabled)
        {
            if(await store.GetAsync(step.TaskId,step.StepId,ct)!=null)throw new InvalidOperationException("原任务绑定新握手契约，执行未启用时禁止降级准备运输。");
            return;
        }
        if(options.PlcContractVersion!=HandshakeExecutionOptions.Version)throw new InvalidOperationException("新握手PLC契约版本未匹配，不准备首段运输。");
        if(map.Current==null)throw new InvalidOperationException("新契约缺少运行地图。");
        var observed=TaskHandshakeSignals.Capture(signals,step.StationId,creation.DumperStationId);
        foreach(var signal in observed)
        {
            signals.TryGet(signal.StationId,signal.VariableName,out var sample);
            if(sample?.Value is not bool value||!TaskSignalPredicate.EvaluateFresh(signal.StationId,signal.VariableName,value,sample,DateTimeOffset.UtcNow,options.StateMaxAgeSeconds).Satisfied)
                throw new InvalidOperationException("首段运输前13个契约信号必须新鲜有效："+signal.StationId+"/"+signal.VariableName);
        }
        if(!Is(step.StationId,"Faulted",false)||!Is(step.StationId,"CommunicationOnline",true)||!Is(creation.DumperStationId,"Faulted",false))throw new InvalidOperationException("设备故障或通讯异常，不准备新契约首段运输。");
        var baseline=observed.Concat(new[]{new TaskSignalEvidence("__context","Binding",null,"Context",DateTimeOffset.UtcNow,null,Binding(step))}).ToArray();
        var bound=await store.BeginAsync(step,HandshakeExecutionOptions.Version,"TransportBound",baseline,ct);
        if(bound.Baseline.FirstOrDefault(b=>b.StationId=="__context")?.Address!=Binding(step))throw new InvalidOperationException("首段原契约绑定与当前配置不一致。");
    }

    public async Task ValidateTransportAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        if(!Enabled)return;
        var priorType=step.StepType switch {DispatchTaskStepType.DeliverFullBoxToDumper=>DispatchTaskStepType.PickupFullBox,DispatchTaskStepType.EnterDumpingPosition=>DispatchTaskStepType.WaitForDumperReady,DispatchTaskStepType.ReturnEmptyBox=>DispatchTaskStepType.DumpFullBox,_=>throw new InvalidOperationException("不支持的运输阶段。")};
        var prior=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==priorType);
        var checkpoint=prior==null?null:await store.GetAsync(step.TaskId,prior.StepId,ct);
        if(checkpoint?.Phase!="ReadyToComplete"||checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.PendingSignal!=null||checkpoint.Baseline.FirstOrDefault(b=>b.StationId=="__context")?.Address!=Binding(step))
            throw new InvalidOperationException("前序持久化握手或契约绑定尚未核对，禁止准备新运输。");
        if(!Is(creation.DumperStationId,"Faulted",false))throw new InvalidOperationException("倒料机故障或反馈无效，禁止准备后续运输。");
        if(step.StepType==DispatchTaskStepType.ReturnEmptyBox&&(!Is(creation.DumperStationId,"DumpingCompletedMayLeave",true)||!Is(creation.DumperStationId,"AgvAtDumpingPosition",true)))
            throw new InvalidOperationException("本轮离开许可或倒料位保持未成立，禁止准备返程。");
    }
    private bool Is(string station,string variable,bool expected)
    {
        signals.TryGet(station,variable,out var s);
        return TaskSignalPredicate.EvaluateFresh(station,variable,expected,s,DateTimeOffset.UtcNow,options.StateMaxAgeSeconds).Satisfied;
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
        if(binding?.ContractVersion!=HandshakeExecutionOptions.Version||binding.Baseline.FirstOrDefault(b=>b.StationId=="__context")?.Address!=Binding(step))
            throw new InvalidOperationException("缺少本任务首段原契约绑定；旧任务禁止自动迁入新握手。");
        var checkpoint=await store.GetAsync(step.TaskId,step.StepId,ct);
        if(checkpoint==null)
        {
            // Any prior notification on first encounter is ambiguous: do not reset/reissue it.
            var observed=TaskHandshakeSignals.Capture(signals,step.StationId,creation.DumperStationId);
            if(observed.Any(s=>s.Value==null||s.Quality!="Good"||s.ObservedAt==null||DateTimeOffset.UtcNow-s.ObservedAt>TimeSpan.FromSeconds(Math.Clamp(options.StateMaxAgeSeconds,1,60))||s.ObservedAt>DateTimeOffset.UtcNow.AddSeconds(1)))
                throw new InvalidOperationException("新契约初始基线需要13个新鲜有效信号；当前缺失/过期，未创建可执行阶段。");
            var baseline=observed.Concat(new[]{new TaskSignalEvidence("__context","Binding",null,"Context",DateTimeOffset.UtcNow,null,Binding(step))}).ToArray();
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
        if(checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.PendingSignal!=null||checkpoint.Phase=="ReviewRequired")
            throw new InvalidOperationException("原握手存在版本冲突、写入结果未知或缺少原始阶段；保留原周期，进入恢复核对，禁止自动重发。");
        if(checkpoint.Baseline.FirstOrDefault(s=>s.StationId=="__context")?.Address!=Binding(step))
            throw new InvalidOperationException("地图、站点映射或动作参数与原握手绑定不一致，禁止静默迁移。");
        foreach(var original in checkpoint.Baseline.Where(b=>b.StationId!="__context"))
            if(!signals.TryGet(original.StationId,original.VariableName,out var current)||current.Address!=original.Address)
                throw new InvalidOperationException("点表映射与原始周期不一致，禁止自动改用新地址。");
        var station=step.StationId;var dumper=creation.DumperStationId;
        if(step.StepType is DispatchTaskStepType.PickupFullBox or DispatchTaskStepType.PlaceEmptyBox or DispatchTaskStepType.CompletePlcHandshake)
            if(!Is(station,"Faulted",false)||!Is(station,"CommunicationOnline",true))throw new InvalidOperationException("来源工位故障、通讯异常或数据无效，保持握手阶段。");
        if(step.StepType is DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.DumpFullBox or DispatchTaskStepType.CompletePlcHandshake)
            if(!Is(dumper,"Faulted",false))throw new InvalidOperationException("倒料机故障或故障反馈无效，保持握手阶段。");
        if(step.StepType is DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.DumpFullBox or DispatchTaskStepType.CompletePlcHandshake)
        {
            var owner=await store.TryClaimEquipmentAsync(step.TaskId,step.StepId,dumper,ct);
            if(owner!=null)throw new InvalidOperationException("倒料机由任务"+owner+"持有，当前任务不消费共享许可。");
        }
        Task Move(string phase,string reason)=>MoveAsync(checkpoint,phase,reason,ct);
        Task Write(string device,string signal,bool value,string next)=>WriteAsync(checkpoint,device,signal,value,next,ct);
        switch(checkpoint.Phase)
        {
            case "AwaitBoxAbsent":
                if(Is(station,"BoxPresent",false))await Move("NotifyRemoved","观察本轮取箱步骤箱体不存在；不替代车端取货终态。");break;
            case "NotifyRemoved":await Write(station,"FullBoxRemoved",true,"NotifyEmptyResponsibility");break;
            case "NotifyEmptyResponsibility":await Write(station,"EmptyBoxTaskAcknowledged",true,"AwaitPickupReadback");break;
            case "AwaitPickupReadback":
                if(Is(station,"BoxPresent",false)&&Is(station,"FullBoxRemoved",true)&&Is(station,"EmptyBoxTaskAcknowledged",true))await Move("ClearCallResponse","输出回读成立；本版本不将回读命名为PLC业务ACK。");break;
            case "ClearCallResponse":await Write(station,"FullBoxRequestAcknowledged",false,"ClearRemoved");break;
            case "ClearRemoved":await Write(station,"FullBoxRemoved",false,"ReadyToComplete");break;
            case "AwaitReadyBaseline":
                if(Is(dumper,"Faulted",false)&&Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"DumpingCompletedMayLeave",false))await Move("NotifyWaiting","设备基线有效；Ready允许提前为1。");break;
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
                var exitProof=await ReturnEvidenceAsync(step,ct);
                if(exitProof!=null&&Is(station,"BoxPresent",true)&&Is(dumper,"Faulted",false))await store.RecordExitAsync(checkpoint,exitProof,ct);break;
            case "ClearDumpingAfterReturn":
                if(await ReturnVerifiedAsync(step,ct))await Write(dumper,"AgvAtDumpingPosition",false,"AwaitDumperClosed");break;
            case "AwaitDumperClosed":
                if(Is(dumper,"AgvAtDumpingPosition",false)&&Is(dumper,"DumpingCompletedMayLeave",false)&&Is(dumper,"AgvAtWaitingPosition",false))await Move("ClearEmptyResponsibility","PLC已结束倒料完成反馈；未向PLC只读输入写0。");break;
            case "ClearEmptyResponsibility":await Write(station,"EmptyBoxTaskAcknowledged",false,"ClearPlaced");break;
            case "ClearPlaced":await Write(station,"EmptyBoxPlaced",false,"ClearRemainingCall");break;
            case "ClearRemainingCall":await Write(station,"FullBoxRequestAcknowledged",false,"ClearRemainingRemoved");break;
            case "ClearRemainingRemoved":await Write(station,"FullBoxRemoved",false,"VerifyClosure");break;
            case "VerifyClosure":
                if(Is(station,"BoxPresent",true)&&new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.All(v=>Is(station,v,false))&&
                    Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"AgvAtDumpingPosition",false)&&Is(dumper,"DumpingCompletedMayLeave",false))await Move("ReadyToComplete","新鲜回读及本轮握手基线闭合；新请求另行受理。");break;
            case "ReadyToComplete":
                var valid=step.StepType switch
                {
                    DispatchTaskStepType.PickupFullBox=>Is(station,"BoxPresent",false)&&Is(station,"FullBoxRemoved",false)&&Is(station,"EmptyBoxTaskAcknowledged",true)&&Is(station,"FullBoxRequestAcknowledged",false),
                    DispatchTaskStepType.WaitForDumperReady=>Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"DumperReady",false),
                    DispatchTaskStepType.DumpFullBox=>Is(dumper,"AgvAtDumpingPosition",true)&&Is(dumper,"DumpingCompletedMayLeave",true),
                    DispatchTaskStepType.PlaceEmptyBox=>Is(station,"BoxPresent",true)&&Is(station,"EmptyBoxPlaced",true),
                    DispatchTaskStepType.CompletePlcHandshake=>Is(station,"BoxPresent",true)&&new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.All(v=>Is(station,v,false))&&Is(dumper,"AgvAtDumpingPosition",false)&&Is(dumper,"AgvAtWaitingPosition",false)&&Is(dumper,"DumpingCompletedMayLeave",false)&&await ReturnVerifiedAsync(step,ct),
                    _=>false
                };
                if(valid&&await tasks.CompleteReadyStepAsync(step.TaskId,step.StepId,ct)&&step.StepType==DispatchTaskStepType.CompletePlcHandshake)
                {await traffic.ReleaseTaskAsync(step.TaskId,ct);await store.ReleaseEquipmentAsync(step.TaskId,ct);}
                break;
            default:throw new InvalidOperationException("未知持久化握手阶段，禁止回退或重跑："+checkpoint.Phase);
        }
        return true;
    }

    private string Binding(DispatchTaskStepWorkItem step)=>JsonSerializer.Serialize(new {map.Current?.MapId,map.Current?.VersionId,Vehicle=step.VehicleId,Pickup=dispatch.GetPickupSite(step.StationId),Waiting=dispatch.GetDumperWaitingSite(),Dump=dispatch.DumperDumpingSite,Dumper=creation.DumperStationId,Actions=dispatch.StepActions.OrderBy(a=>a.Key).ToArray()});

    private async Task<bool> ReturnVerifiedAsync(DispatchTaskStepWorkItem step,CancellationToken ct)=>await ReturnEvidenceAsync(step,ct)!=null;
    private async Task<TaskExitEvidence?> ReturnEvidenceAsync(DispatchTaskStepWorkItem step,CancellationToken ct)
    {
        var returned=(await taskReader.GetStepsAsync(step.TaskId,ct)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
        if(returned?.Status!=DispatchTaskStepStatus.Completed||string.IsNullOrWhiteSpace(returned.VendorTaskId)||step.VehicleId==null||
            !vehicles.TryGet(step.VehicleId,out var vehicle)||vehicle.Connection!=ConnectionState.Online||vehicle.Quality!=SignalQuality.Good||vehicle.LastSuccessfulPollAt is not {} time||
            DateTimeOffset.UtcNow-time>TimeSpan.FromSeconds(Math.Clamp(options.StateMaxAgeSeconds,1,60))||time>DateTimeOffset.UtcNow.AddSeconds(1))return null;
        var pickup=dispatch.GetPickupSite(step.StationId);
        var definition=map.Current?.Definition;
        var node=definition?.Nodes.FirstOrDefault(n=>n.Id==pickup);
        if(node?.AgvSiteId is not {} vendorSite||vendorSite.Length==0||pickup==dispatch.DumperDumpingSite||vehicle.CurrentStation!=vendorSite||vehicle.EmergencyStopped!=false||vehicle.Blocked!=false)return null;
        return new(step.TaskId,step.StepId,step.VehicleId,returned.StepId,returned.VendorTaskId!,vendorSite,pickup!,map.Current!.MapId,map.Current.VersionId,time,DateTimeOffset.UtcNow);
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
