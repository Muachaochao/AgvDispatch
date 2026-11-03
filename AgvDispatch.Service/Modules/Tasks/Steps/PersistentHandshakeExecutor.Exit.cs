using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Traffic;

namespace AgvDispatch.Service;

/// <summary>Valid observations retain ownership while the vehicle's full envelope leaves the area.</summary>
public sealed class DumperExitPendingException(string message) : InvalidOperationException(message);

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
        if(binding?.ContractVersion!=HandshakeExecutionOptions.Version||!BindingMatches(binding,step))
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
        if(checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.PendingSignal!=null||checkpoint.Phase=="ReviewRequired"||!BindingMatches(checkpoint,step))
            throw new InvalidOperationException("出区原阶段冲突或写入结果未知，需要恢复核对，禁止重发。");
        if(checkpoint.Phase=="ExitHandoverComplete")
        {
            _exitSamples.Remove(step.StepId);
            await traffic.ReleaseTaskExceptStepAsync(task.TaskId,step.StepId,ct);
            return;
        }
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
                if(await CaptureExitProofAsync(checkpoint,step,returned.VendorTaskId!,ct) is {} proof)
                    await store.RecordPhysicalExitAsync(checkpoint,proof,ct);
                break;
            case "ClearDumpingAfterExit":
                if(await CaptureExitProofAsync(checkpoint,step,returned.VendorTaskId!,ct) is {} fresh)
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
                if(DumperClosed()&&await CaptureExitProofAsync(checkpoint,step,returned.VendorTaskId!,ct) is {} finalProof)
                {
                    if(!DumperClosed()||!Is(dumper,"Faulted",false)||!await store.OwnsEquipmentAsync(task.TaskId,dumper,ct))return;
                    await store.ReleaseDumperAfterExitAsync(checkpoint,dumper,finalProof,ct);
                }
                break;
            default:throw new InvalidOperationException("未知实际出区阶段，禁止重新解释："+checkpoint.Phase);
        }
    }

    private bool DumperClosed()=>new[]{"AgvAtDumpingPosition","AgvAtWaitingPosition","DumperReady","DumpingCompletedMayLeave"}.All(v=>Is(creation.DumperStationId,v,false));

    private async Task<TaskDumperExitProof?> CaptureExitProofAsync(HandshakeCheckpoint checkpoint,DispatchTaskStepWorkItem step,string vendorTaskId,CancellationToken ct)
    {
        try { return await CaptureExitProofCoreAsync(step,vendorTaskId,ct); }
        catch(InvalidOperationException exception)
        {
            // This only records why evidence collection is waiting. It neither advances
            // the phase nor acknowledges an uncertain write or releases ownership.
            var reason="出区核验等待："+exception.Message;
            if(checkpoint.Reason!=reason)
                await store.MoveAsync(checkpoint,checkpoint.Phase,reason,null,null,null,ct);
            throw;
        }
    }

    private async Task<TaskDumperExitProof?> CaptureExitProofCoreAsync(DispatchTaskStepWorkItem step,string vendorTaskId,CancellationToken ct)
    {
        ValidateExitConfiguration(step);
        var current=map.Current!;
        var vehicleId=step.VehicleId??throw new InvalidOperationException("原返程未绑定车辆，不能核验出区。");
        var profile=options.DumperExit.BodyProfiles.Single(p=>p.VehicleId==vehicleId);
        DumperExitFrame ReadFrame()
        {
            if(!ReferenceEquals(current,map.Current))throw new InvalidOperationException("采集出区证据期间运行地图已变化，保持原占用。");
            if(!vehicles.TryGet(vehicleId,out var state)||state.X is not {} x||state.Y is not {} y||state.LocalizationConfidence is not {} confidence||state.LastSuccessfulPollAt is not {} observed||state.Blocked is null||state.EmergencyStopped is null)
            {
                _exitSamples.Remove(step.StepId);
                throw new InvalidOperationException("车辆定位、采集时间、阻塞或急停状态缺失，不能核验出区。");
            }
            if(AgvObstaclePolicy.HasBlockingAlarm(state))
            {
                _exitSamples.Remove(step.StepId);
                throw new InvalidOperationException("车辆存在严重报警，保持设备与路权，不能核验出区。");
            }
            return new(vehicleId,current.MapId,current.VersionId,x,y,confidence,observed,state.Quality,state.Connection==ConnectionState.Online,AgvObstaclePolicy.IsBlocked(state),state.EmergencyStopped.Value);
        }
        void RequireVerified(DumperExitFrame? first,DumperExitFrame second,DateTimeOffset now)
        {
            var assessed=DumperExitEvaluator.Evaluate(current.Definition,first,second,profile,options.DumperExit.Policy,now);
            if(assessed.WaitingForPhysicalExit)throw new DumperExitPendingException(assessed.Reason);
            if(!assessed.Verified)throw new InvalidOperationException(assessed.Reason+
                (first is not null&&(first.LocalizationConfidence<options.DumperExit.MinimumConfidence||second.LocalizationConfidence<options.DumperExit.MinimumConfidence)
                    ?$" 要求定位置信度至少{options.DumperExit.MinimumConfidence:0.###}。":""));
        }
        var frame=ReadFrame();
        var minimum=options.DumperExit.MinimumObservationIntervalSeconds;
        if(!_exitSamples.TryGetValue(step.StepId,out var pair)){_exitSamples[step.StepId]=(null,frame);return null;}
        if(frame.ObservedAt<pair.Second.ObservedAt||frame.ObservedAt==pair.Second.ObservedAt&&frame!=pair.Second)
        {
            _exitSamples.Remove(step.StepId);
            throw new InvalidOperationException("出区定位时间倒退或同一时刻的车辆状态冲突，等待重新采集。");
        }
        if(frame.ObservedAt>pair.Second.ObservedAt)
        {
            // A newer observation always vetoes reuse of an older outside/outside pair.
            if((frame.ObservedAt-pair.Second.ObservedAt).TotalSeconds<minimum)return null;
            _exitSamples[step.StepId]=pair=(pair.Second,frame);
        }
        if(pair.First is null)return null;
        RequireVerified(pair.First,pair.Second,DateTimeOffset.UtcNow);
        var mapCheck=await mapPreflight!.CheckCurrentMapAsync(vehicleId,current.MapId,current.VersionId,ct);
        if(!mapCheck.Accepted)throw new InvalidOperationException("实际出区车端地图核验未通过："+mapCheck.Message);
        var vendor=await agvGateway!.GetTransportTaskStatusAsync(vendorTaskId,ct);
        var now=DateTimeOffset.UtcNow;
        if(vendor==null||vendor.VendorTaskId!=vendorTaskId||vendor.BusinessTaskId!=step.StepId||vendor.VehicleId!=vehicleId||
            vendor.State is not (AgvTransportTaskState.GoingToDestination or AgvTransportTaskState.AtDestination or AgvTransportTaskState.Completed)||
            vendor.UpdatedAt>now.AddSeconds(1)||now-vendor.UpdatedAt>TimeSpan.FromSeconds(options.DumperExit.MaximumAgeSeconds))
            throw new InvalidOperationException("原返程任务身份、执行状态或新鲜查询不成立，不能以其他任务/未知状态交接。");
        var lastFrame=ReadFrame();
        if(lastFrame.ObservedAt<pair.Second.ObservedAt||lastFrame.ObservedAt==pair.Second.ObservedAt&&lastFrame!=pair.Second)
        {
            _exitSamples.Remove(step.StepId);
            throw new InvalidOperationException("车端查询期间定位时间倒退或状态冲突，等待重新采集。");
        }
        if(lastFrame.ObservedAt>pair.Second.ObservedAt)pair=(pair.Second,lastFrame);
        // A map inspection can take longer than the position evidence lifetime. Never
        // keep its pre-query first frame and retry the same doomed pair forever.
        // Recollect after the queries, without extending the vendor-query deadline.
        var maximumAge=TimeSpan.FromSeconds(options.DumperExit.MaximumAgeSeconds);
        if(pair.First is null||now-pair.First.ObservedAt>maximumAge)
        {
            var first=lastFrame;
            _exitSamples[step.StepId]=(null,first);
            var deadline=vendor.UpdatedAt+maximumAge;
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                now=DateTimeOffset.UtcNow;
                if(now>=deadline)throw new InvalidOperationException("车端查询后未在原任务查询有效期内取得两次新鲜出区定位；保持占用，等待采集恢复。");
                await Task.Delay(TimeSpan.FromMilliseconds(25),ct);
                var second=ReadFrame();
                if(second.ObservedAt<first.ObservedAt||second.ObservedAt==first.ObservedAt&&second!=first)
                    throw new InvalidOperationException("重新采集出区定位时发生时间倒退或状态冲突。");
                if(second.ObservedAt==first.ObservedAt||(second.ObservedAt-first.ObservedAt).TotalSeconds<minimum)continue;
                pair=(first,second);
                break;
            }
        }
        now=DateTimeOffset.UtcNow;
        _exitSamples[step.StepId]=pair;
        RequireVerified(pair.First,pair.Second,now);
        if(vendor.UpdatedAt>now.AddSeconds(1)||now-vendor.UpdatedAt>maximumAge)
            throw new InvalidOperationException("重新采集定位后原返程查询已过期，保持占用。");
        vehicles.TryGet(vehicleId,out var endpointVehicle);
        var endpointStopped=endpointVehicle is not null&&endpointVehicle.CurrentStation==step.DestinationSite&&
            endpointVehicle.VelocityX is {} vx&&endpointVehicle.VelocityY is {} vy&&endpointVehicle.AngularVelocity is {} va&&
            Math.Abs(vx)<=.02&&Math.Abs(vy)<=.02&&Math.Abs(va)<=.02;
        return new(step.TaskId,step.StepId,vehicleId,vendorTaskId,current.MapId,current.VersionId,
            EngineeringRuntimeIdentity.MapHash(current.Definition),pair.First!,pair.Second,profile,
            options.DumperExit.Policy,options.DumperExit.MaximumRouteDeviationMeters,vendor.UpdatedAt,now)
            {PlcSignals=TaskHandshakeSignals.Capture(signals,step.StationId,creation.DumperStationId).Where(s=>s.StationId==creation.DumperStationId).ToArray(),
             EndpointArrivalConfirmed=vendor.State==AgvTransportTaskState.Completed&&endpointStopped};
    }
}
