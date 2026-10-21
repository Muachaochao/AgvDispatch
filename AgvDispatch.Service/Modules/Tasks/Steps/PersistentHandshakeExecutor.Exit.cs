using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Traffic;

namespace AgvDispatch.Service;

public sealed partial class PersistentHandshakeExecutor
{
    private readonly Dictionary<string,(DumperExitFrame? First,DumperExitFrame Second)> _exitSamples=new(StringComparer.Ordinal);

    private void ValidateExitConfiguration(DispatchTaskStepWorkItem step)
    {
        if(agvGateway==null||mapPreflight==null)throw new InvalidOperationException("28阶段出区核验需要原任务查询与车端当前地图核验接口。");
        var current=map.Current??throw new InvalidOperationException("缺少出区核验地图。");
        var profiles=options.DumperExit.BodyProfiles.Where(p=>p.VehicleId==step.VehicleId).ToArray();
        if(profiles.Length!=1)throw new InvalidOperationException("28阶段需要本车唯一的已核定整车及最大载荷包络。");
        var assessment=DumperExitEvaluator.ValidateConfiguration(current.Definition,step.VehicleId??"",profiles[0],options.DumperExit.Policy);
        if(!assessment.Verified)throw new InvalidOperationException(assessment.Reason);
        if(!double.IsFinite(options.DumperExit.MaximumRouteDeviationMeters)||options.DumperExit.MaximumRouteDeviationMeters<=0||options.DumperExit.MaximumRouteDeviationMeters>2)
            throw new InvalidOperationException("原返程路线偏离阈值无效。");
        var dump=current.Definition.Nodes.FirstOrDefault(n=>n.Id==dispatch.DumperDumpingSite||n.AgvSiteId==dispatch.DumperDumpingSite);
        if(dump==null||!dump.ResourceIds.Any(id=>options.DumperExit.ResourceIds.Contains(id,StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("实际倒料节点未关联指定出区资源，禁止把无关区域当作倒料区交接。");
    }

    /// <summary>Runs independently while the original return transport is executing.</summary>
    public async Task ProcessExitAsync(DispatchTaskWorkItem task,CancellationToken ct)
    {
        if(!Enabled||task.Stage is not (DispatchTaskStage.ReturningEmptyBox or DispatchTaskStage.WaitingForPlcConfirmation))return;
        if(options.PlcContractVersion!=HandshakeExecutionOptions.Version)throw new InvalidOperationException("28阶段契约未启用，禁止出区清位或交接。");
        var steps=await taskReader.GetStepsAsync(task.TaskId,ct);
        var returned=steps.FirstOrDefault(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
        if(returned?.Status is not (DispatchTaskStepStatus.Executing or DispatchTaskStepStatus.Completed)||string.IsNullOrWhiteSpace(returned.VendorTaskId))return;
        var step=new DispatchTaskStepWorkItem(task.TaskId,task.StationId,task.VehicleId,task.Stage,returned.StepId,returned.Sequence,returned.StepType,returned.Status,returned.SourceSite,returned.DestinationSite,returned.UpdatedAt);
        var first=steps.FirstOrDefault(s=>s.StepType==DispatchTaskStepType.GoToPickup);
        var binding=first==null?null:await store.GetAsync(task.TaskId,first.StepId,ct);
        if(binding?.ContractVersion!=HandshakeExecutionOptions.Version||binding.Baseline.FirstOrDefault(s=>s.StationId=="__context")?.Address!=Binding(step))
            throw new InvalidOperationException("返程原契约与当前地图、出区标定或配置不一致，保持占用。");
        var checkpoint=await store.GetAsync(task.TaskId,returned.StepId,ct);
        if(checkpoint==null)
        {
            var dumping=steps.FirstOrDefault(s=>s.StepType==DispatchTaskStepType.DumpFullBox);
            var prior=dumping==null?null:await store.GetAsync(task.TaskId,dumping.StepId,ct);
            if(prior?.Phase!="ReadyToComplete"||prior.ContractVersion!=HandshakeExecutionOptions.Version||prior.PendingSignal!=null||
                !Is(creation.DumperStationId,"AgvAtDumpingPosition",true)||!Is(creation.DumperStationId,"DumpingCompletedMayLeave",true))
                throw new InvalidOperationException("缺少原倒料完成握手，不能补造出区周期。");
            checkpoint=await store.BeginAsync(step,HandshakeExecutionOptions.Version,"AwaitPhysicalExit",binding.Baseline,ct);
        }
        if(checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.PendingSignal!=null||checkpoint.Phase=="ReviewRequired"||checkpoint.Baseline.FirstOrDefault(s=>s.StationId=="__context")?.Address!=Binding(step))
            throw new InvalidOperationException("出区原阶段冲突或写入结果未知，需要恢复核对，禁止重发。");
        if(checkpoint.Phase=="ExitHandoverComplete"){_exitSamples.Remove(step.StepId);return;}
        foreach(var original in checkpoint.Baseline.Where(s=>s.StationId!="__context"))
            if(!signals.TryGet(original.StationId,original.VariableName,out var value)||original.Address!=value.Address)
                throw new InvalidOperationException("出区原点表已变化，保持原设备责任。");
        var dumper=creation.DumperStationId;
        if(!Is(dumper,"Faulted",false))throw new InvalidOperationException("出区期间倒料设备故障或数据无效，保持占用。");
        if(!await store.OwnsEquipmentAsync(task.TaskId,dumper,ct))throw new InvalidOperationException("出区设备原归属缺失或不一致，禁止重新认领或改动后车周期。");
        switch(checkpoint.Phase)
        {
            case "AwaitPhysicalExit":
                if(!Is(dumper,"AgvAtDumpingPosition",true)||!Is(dumper,"DumpingCompletedMayLeave",true))return;
                if(await CaptureExitProofAsync(step,returned.VendorTaskId!,ct) is {} proof)
                    await store.RecordPhysicalExitAsync(checkpoint,proof,ct);
                break;
            case "ClearDumpingAfterExit":
                if(await CaptureExitProofAsync(step,returned.VendorTaskId!,ct) is {} fresh)
                {
                    await store.ValidatePhysicalExitAsync(checkpoint,fresh,ct);
                    if(!await store.OwnsEquipmentAsync(task.TaskId,dumper,ct)||!Is(dumper,"Faulted",false)||!Is(dumper,"AgvAtDumpingPosition",true)||!Is(dumper,"DumpingCompletedMayLeave",true))return;
                    await WriteAsync(checkpoint,dumper,"AgvAtDumpingPosition",false,"AwaitDumperClosed",ct);
                }
                break;
            case "AwaitDumperClosed":
                if(DumperClosed())await MoveAsync(checkpoint,"ReleaseDumper","PLC已撤销Done且共享输出、Ready均闭合；待原子交接。",ct);
                break;
            case "ReleaseDumper":
                if(DumperClosed()&&await CaptureExitProofAsync(step,returned.VendorTaskId!,ct) is {} finalProof)
                {
                    if(!DumperClosed()||!Is(dumper,"Faulted",false)||!await store.OwnsEquipmentAsync(task.TaskId,dumper,ct))return;
                    await store.ReleaseDumperAfterExitAsync(checkpoint,dumper,finalProof,ct);
                }
                break;
            default:throw new InvalidOperationException("未知实际出区阶段，禁止重新解释："+checkpoint.Phase);
        }
    }

    private bool DumperClosed()=>new[]{"AgvAtDumpingPosition","AgvAtWaitingPosition","DumperReady","DumpingCompletedMayLeave"}.All(v=>Is(creation.DumperStationId,v,false));

    private async Task<TaskDumperExitProof?> CaptureExitProofAsync(DispatchTaskStepWorkItem step,string vendorTaskId,CancellationToken ct)
    {
        ValidateExitConfiguration(step);
        var current=map.Current!;
        if(step.VehicleId==null||!vehicles.TryGet(step.VehicleId,out var state)||state.X is not {} x||state.Y is not {} y||state.LocalizationConfidence is not {} confidence||state.LastSuccessfulPollAt is not {} observed||state.Blocked is not false||state.EmergencyStopped is not false){_exitSamples.Remove(step.StepId);return null;}
        var frame=new DumperExitFrame(step.VehicleId,current.MapId,current.VersionId,x,y,confidence,observed,state.Quality,state.Connection==ConnectionState.Online,false,false);
        var minimum=options.DumperExit.MinimumObservationIntervalSeconds;
        if(!_exitSamples.TryGetValue(step.StepId,out var pair)){_exitSamples[step.StepId]=(null,frame);return null;}
        if(frame.ObservedAt<pair.Second.ObservedAt||frame.ObservedAt==pair.Second.ObservedAt&&frame!=pair.Second){_exitSamples.Remove(step.StepId);return null;}
        if(frame.ObservedAt>pair.Second.ObservedAt)
        {
            // A newer observation always vetoes reuse of an older outside/outside pair.
            if((frame.ObservedAt-pair.Second.ObservedAt).TotalSeconds<minimum)return null;
            _exitSamples[step.StepId]=pair=(pair.Second,frame);
        }
        var profile=options.DumperExit.BodyProfiles.Single(p=>p.VehicleId==step.VehicleId);
        var assessment=DumperExitEvaluator.Evaluate(current.Definition,pair.First,pair.Second,profile,options.DumperExit.Policy,DateTimeOffset.UtcNow);
        if(!assessment.Verified)return null;
        var mapCheck=await mapPreflight!.CheckCurrentMapAsync(step.VehicleId,current.MapId,current.VersionId,ct);
        if(!mapCheck.Accepted)throw new InvalidOperationException("实际出区车端地图核验未通过："+mapCheck.Message);
        var vendor=await agvGateway!.GetTransportTaskStatusAsync(vendorTaskId,ct);
        var now=DateTimeOffset.UtcNow;
        if(vendor==null||vendor.VendorTaskId!=vendorTaskId||vendor.BusinessTaskId!=step.StepId||vendor.VehicleId!=step.VehicleId||
            vendor.State is not (AgvTransportTaskState.GoingToDestination or AgvTransportTaskState.AtDestination or AgvTransportTaskState.Completed)||
            vendor.UpdatedAt>now.AddSeconds(1)||now-vendor.UpdatedAt>TimeSpan.FromSeconds(options.DumperExit.MaximumAgeSeconds))
            throw new InvalidOperationException("原返程任务身份、执行状态或新鲜查询不成立，不能以其他任务/未知状态交接。");
        if(!ReferenceEquals(current,map.Current)||!vehicles.TryGet(step.VehicleId,out var latest)||latest.X is not {} lx||latest.Y is not {} ly||latest.LocalizationConfidence is not {} lc||latest.LastSuccessfulPollAt is not {} lt||latest.Blocked is not false||latest.EmergencyStopped is not false)return null;
        var lastFrame=new DumperExitFrame(step.VehicleId,current.MapId,current.VersionId,lx,ly,lc,lt,latest.Quality,latest.Connection==ConnectionState.Online,false,false);
        if(lt<pair.Second.ObservedAt||lt==pair.Second.ObservedAt&&lastFrame!=pair.Second){_exitSamples.Remove(step.StepId);return null;}
        if(lt>pair.Second.ObservedAt)pair=(pair.First,lastFrame);
        if(!DumperExitEvaluator.Evaluate(current.Definition,pair.First,pair.Second,profile,options.DumperExit.Policy,now).Verified)return null;
        return new(step.TaskId,step.StepId,step.VehicleId,vendorTaskId,current.MapId,current.VersionId,
            TransportRouteSnapshots.Hash(JsonSerializer.Serialize(current.Definition,new JsonSerializerOptions(JsonSerializerDefaults.Web))),pair.First!,pair.Second,profile,
            options.DumperExit.Policy,options.DumperExit.MaximumRouteDeviationMeters,vendor.UpdatedAt,now)
            {PlcSignals=TaskHandshakeSignals.Capture(signals,step.StationId,creation.DumperStationId).Where(s=>s.StationId==creation.DumperStationId).ToArray()};
    }
}
