using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

public sealed partial class ChargingService
{
    private bool CanHandleSite(ChargingSite site,string vehicleId) => site.ControlMode == "Relay6005" ||
        site.ControlMode == "RobokitScript" && workflow?.IsConfigured(site,vehicleId) == true;

    public async Task<ChargingResult> RequestAsync(TrafficUserContext user,ChargingRequest request,CancellationToken ct)
    {
        if(!user.Has(TrafficPermission.Administer))throw new UnauthorizedAccessException("充电申请需要现场操作权限。");
        if(!Guid.TryParse(request.RequestId,out var requestGuid)||string.IsNullOrWhiteSpace(request.VehicleId))
            return new(false,"请提供唯一申请编号和车辆编号。");
        if(request.RequiredPositiveSeconds is {} seconds && (seconds is <60 or >600))
            return new(false,"计时联调须为连续有效充电60～600秒。");
        var requestId=requestGuid.ToString("N");
        var vehicleId=request.VehicleId.Trim();
        var returnSite=string.IsNullOrWhiteSpace(request.ReturnSiteId)?null:request.ReturnSiteId.Trim();
        using var lease=await EnterBusinessAsync(ct);
        // Retained request IDs are checked before enablement: a lost HTTP reply
        // cannot turn a subsequent policy change into a second operation.
        if(sessions.SingleOrDefault(s=>s.RequestId==requestId) is {} prior)
            return prior.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase)&&
                prior.RequiredPositiveSeconds==request.RequiredPositiveSeconds&&prior.RequestedReturnSiteId==returnSite
                ?new(true,"原充电申请已受理，保留原会话："+prior.SessionId)
                :new(false,"同一申请编号的车辆、计时或返程点已改变，拒绝重复受理。");
        if(!options.Enabled)return new(false,"后台自动充电尚未启用，请先完成充电位、车辆标定和运行配置。");
        if(request.RequiredPositiveSeconds is not null&&!options.AllowTimedTests)
            return new(false,"计时联调未启用；常规充电按目标电量结束。");
        if(!options.Sites.Any(s=>s.Commissioned&&s.AllowedVehicleIds.Contains(vehicleId,StringComparer.OrdinalIgnoreCase)&&CanHandleSite(s,vehicleId)))
            return new(false,"当前车辆没有已核定并接通执行器的充电位。");
        if(port.Vehicles.FirstOrDefault(v=>v.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase)) is not {} vehicle||
            vehicle.Connection!=ConnectionState.Online||!Fresh(vehicle.LastSuccessfulPollAt))
            return new(false,"车辆实时状态未取得、离线或过期，尚未创建充电需求。");
        if(sessions.Any(s=>s.Phase!=ChargingPhase.Completed&&s.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase)))
            return new(false,"该车辆已有未闭合充电会话，请查询原会话。");
        var row=Save(new("CHARGE-"+Guid.NewGuid().ToString("N"),vehicleId,ChargingPhase.Queued,Now,Now,
            "人工充电需求已受理；等待当前箱体循环和设备责任安全交接，不立即抢走车辆。")
        {
            RequestId=requestId,TargetBatteryPercent=options.TargetBatteryPercent,
            RequiredPositiveSeconds=request.RequiredPositiveSeconds,RequestedReturnSiteId=returnSite,
            Operator=user.WindowsIdentity
        });
        return new(true,"充电需求已进入队列："+row.SessionId);
    }

    private static bool IsScriptRunning(ChargingPhase phase) => phase is ChargingPhase.Docking or
        ChargingPhase.SessionRunning or ChargingPhase.Undocking;

    private async Task BeginScriptAsync(ChargingSession row,ChargingWorkflowKind kind,CancellationToken ct)
    {
        if(workflow is null||!options.Enabled||port.NewWorkBlockReason is not null||!CanHandleSite(row.Site!,row.VehicleId))
        {Hold(row,"脚本执行许可、标定或运行门禁尚未放行，保留原会话。");return;}
        var error=await port.ValidateAsync(row,true,ct)??await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct);
        if(error is not null)
        {Hold(row,error);return;}
        if(kind==ChargingWorkflowKind.Undock&&!row.ShutdownVerified)
        {Hold(row,"车桩停止充电反馈尚未闭合，禁止发送退桩动作。");return;}
        var operationId=row.SessionId+"-"+kind.ToString().ToUpperInvariant();
        var sending=kind switch {ChargingWorkflowKind.Dock=>ChargingPhase.SendingDock,
            ChargingWorkflowKind.Charge=>ChargingPhase.StartingSession,_=>ChargingPhase.SendingUndock};
        var running=kind switch {ChargingWorkflowKind.Dock=>ChargingPhase.Docking,
            ChargingWorkflowKind.Charge=>ChargingPhase.SessionRunning,_=>ChargingPhase.Undocking};
        // This intent is stored BEFORE a process can upload or execute a script.
        row=Move(row with {ScriptOperationId=operationId,ScriptOperationKind=kind,WorkflowStatus=null,
            ChargeOperationId=kind==ChargingWorkflowKind.Charge?operationId:row.ChargeOperationId,
            StopAttempted=false,StopRequested=false,
            StartAttempted=row.StartAttempted||kind==ChargingWorkflowKind.Charge},sending,
            "原脚本阶段及唯一操作编号已持久化，准备一次执行："+kind);
        await workflow.StartAsync(row,operationId,kind,ct);
        Move(row,running,"脚本阶段已交给执行器，持续查询原操作："+kind);
    }

    private async Task<ChargingWorkflowStatus> ObserveScriptAsync(ChargingSession row,CancellationToken ct)
    {
        var observed=await (workflow??throw new InvalidOperationException("脚本执行器未注册。")).ObserveAsync(row,ct);
        if(observed.OperationId!=row.ScriptOperationId||observed.Kind!=row.ScriptOperationKind||!Enum.IsDefined(observed.State))
            throw new InvalidDataException("脚本回执与原操作编号或阶段不一致。");
        if(observed.UpdatedAt>Now.AddSeconds(1)||
            observed.State==ChargingWorkflowState.Running&&!Fresh(observed.UpdatedAt))
            throw new InvalidDataException("脚本执行心跳过期或时间无效，禁止推断完成。");
        if(observed.Battery is {} battery&&(battery.VehicleId!=row.VehicleId||
            battery.BatteryPercent is not >=0 or >100||battery.Current is {} current&&!double.IsFinite(current)))
            throw new InvalidDataException("脚本电池回执身份或数值无效。");
        return observed;
    }

    private async Task AdvanceScriptAsync(ChargingSession row,CancellationToken ct)
    {
        if(IsScriptRunning(row.Phase))
        {
            var status=await ObserveScriptAsync(row,ct);
            row=Save(row with {WorkflowStatus=status,Battery=status.Battery??row.Battery,UpdatedAt=Now});
            if(status.State is ChargingWorkflowState.Unknown or ChargingWorkflowState.Failed)
            {Hold(row,"脚本阶段未完成，保留车辆、充电位及路权："+status.Message);return;}
            if(status.State==ChargingWorkflowState.Running)
            {
                if(!options.Enabled||port.NewWorkBlockReason is not null||
                    await port.ValidateAsync(row,false,ct) is not null||
                    TimedOut(row,row.Phase==ChargingPhase.SessionRunning?options.ChargingTimeoutSeconds+30:options.NavigationTimeoutSeconds))
                {
                    await StopScriptAsync(row,"运行许可、车辆状态或阶段时限发生变化，停止原脚本并保留资源。",ct);
                }
                return;
            }
            if(row.StopRequested)
            {Hold(row,"原脚本已停止，等待现场核对后退离或关闭；不自动恢复动作。");return;}
            if(row.Phase==ChargingPhase.Docking)
            {await BeginScriptAsync(row,ChargingWorkflowKind.Charge,ct);return;}
            if(row.Phase==ChargingPhase.SessionRunning)
            {
                var enough=status.ContinuousPositiveSeconds >= (row.RequiredPositiveSeconds??60);
                var targetReached=row.RequiredPositiveSeconds is not null||status.Battery?.BatteryPercent >= (row.TargetBatteryPercent??options.TargetBatteryPercent);
                if(!status.ShutdownVerified||!enough||!targetReached)
                {Hold(row,"有效充电时长、目标电量或车桩停充核验未通过，禁止退车。");return;}
                row=Save(row with {ShutdownVerified=true,UpdatedAt=Now});
                await BeginScriptAsync(row,ChargingWorkflowKind.Undock,ct);return;
            }
            if(!row.ShutdownVerified||!port.IsAt(row.VehicleId,row.Site!.ChargingSiteId))
            {Hold(row,"退桩尚未确认回到原接近点并停稳，保留原操作和充电位。");return;}
            await SendNavigationAsync(row,true,ct);return;
        }
        if(row.Phase is ChargingPhase.SendingDock or ChargingPhase.StartingSession or ChargingPhase.SendingUndock)
        {Hold(row,"原脚本启动结果不确定，只允许查询原操作，禁止补发。");return;}
        if(row.Phase==ChargingPhase.AwaitingStop)
        {
            // Only an explicit Hold review can reach this after an operator stop.
            await BeginScriptAsync(row,ChargingWorkflowKind.Undock,ct);return;
        }
        if(await port.ValidateAsync(row,row.Phase==ChargingPhase.Reserved,ct) is {} validation)
        {Hold(row,validation);return;}
        if(row.Phase==ChargingPhase.Reserved)
        {await SendNavigationAsync(row,false,ct);return;}
        if(row.Phase is ChargingPhase.SendingToCharger or ChargingPhase.SendingExit or ChargingPhase.SendingReturn)
        {Hold(row,"保留未确认的原导航发送意图，禁止自动重发。");return;}
        if(row.Phase is not (ChargingPhase.GoingToCharger or ChargingPhase.Exiting or ChargingPhase.Returning))return;
        var returning=row.Phase==ChargingPhase.Returning;
        var exiting=row.Phase==ChargingPhase.Exiting;
        var path=returning?row.Route!.ReturnPath:exiting?row.Route!.ExitPath:row.Route!.ToCharger;
        var observed=await port.ObserveNavigationAsync(row.VendorTaskId??row.CommandId!,row.VehicleId,path,ct);
        if(observed is null||observed.VendorTaskId!=(row.VendorTaskId??row.CommandId)||observed.VehicleId!=row.VehicleId||
            !Fresh(observed.UpdatedAt)||observed.State==AgvTransportTaskState.Unknown)
        {Hold(row,"原充电导航全链回执不明，只查询原任务，不重复派车。");return;}
        if(observed.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled or AgvTransportTaskState.Suspended)
        {Hold(row,"原充电导航被中止，保留资源："+observed.State);return;}
        if(TimedOut(row,options.NavigationTimeoutSeconds))
        {Hold(row,"原充电导航超时，保留原链及充电位。");return;}
        if(observed.State!=AgvTransportTaskState.Completed)return;
        var target=returning?row.Route!.ReturnSiteId:exiting?row.Site!.ExitSiteId:row.Site!.ChargingSiteId;
        if(target is null||!port.IsAt(row.VehicleId,target))return;
        if(!exiting&&!returning)
        {await BeginScriptAsync(row,ChargingWorkflowKind.Dock,ct);return;}
        if(!row.ShutdownVerified)
        {Hold(row,"原会话未取得完整停充核验，禁止释放充电资源。");return;}
        var reading=await ReadBatteryAsync(row,ct);
        if(!CurrentStopped(reading)||reading.ManualCharge!=false||reading.AutoCharge==true)
        {Hold(row,"退出或返程后仍有充电信号，保留原车辆和资源。");return;}
        row=Save(row with {Battery=reading,UpdatedAt=Now});
        if(exiting&&row.Route!.ReturnPath.Length>=2)
        {await SendReturnAsync(row,ct);return;}
        if(row.Route!.ReturnSiteId is not {} home||!port.IsAt(row.VehicleId,home))
        {Hold(row,"充电已结束但尚未返回本轮原待机点，暂不恢复业务派车。");return;}
        await CompleteAsync(row,"已完成充电、核验停充并返回原待机点；释放充电资源，允许业务恢复。",ct);
    }

    private async Task SendReturnAsync(ChargingSession row,CancellationToken ct)
    {
        var scriptMode=row.Site?.ControlMode=="RobokitScript";
        // Relay mode proves the stop by the battery reading taken at the exit; script mode keeps its shutdown evidence.
        if(scriptMode&&!row.ShutdownVerified||row.Route is not {ReturnPath.Length:>=2})
        {Hold(row,"返程路径或停充证据缺失，保留充电资源。");return;}
        var check=scriptMode?row:row with {Phase=ChargingPhase.SendingReturn};
        var error=await port.ValidateAsync(check,true,ct)??await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct);
        if(error is not null)
        {Hold(row,error);return;}
        var commandId=row.SessionId+"-RETURN";
        row=Move(row with {CommandId=commandId,VendorTaskId=null},ChargingPhase.SendingReturn,
            "原待机点、返程全链和唯一导航编号已持久化，准备一次发送。");
        var vendor=await port.NavigateAsync(commandId,row.VehicleId,row.Route!.ReturnPath,ct);
        if(vendor!=commandId)throw new InvalidDataException("返程导航身份与原请求不符。");
        Move(row with {VendorTaskId=vendor},ChargingPhase.Returning,"前往本轮原待机点："+row.Route.ReturnSiteId);
    }

    private async Task StopScriptAsync(ChargingSession row,string reason,CancellationToken ct)
    {
        if(row.StopAttempted){Hold(row,"原停止已申请，只查询原结果；"+reason);return;}
        row=Save(row with {StopAttempted=true,StopRequested=true,UpdatedAt=Now,Message=reason});
        await (workflow??throw new InvalidOperationException("脚本执行器未注册。")).RequestStopAsync(row,ct);
        Hold(row,"原脚本停止请求已记录，保留资源等待停充、停稳核验；"+reason);
    }

    private async Task<string?> ScriptCloseBlockAsync(ChargingSession row,CancellationToken ct)
    {
        if(row.ScriptOperationId is null)return row.StartAttempted?"缺少原充电操作证据，不能核定关闭。":null;
        var status=await ObserveScriptAsync(row,ct);
        if(status.State is ChargingWorkflowState.Running or ChargingWorkflowState.Unknown)
            return "原脚本仍在运行或结果未知，须先核对原操作停止结果。";
        if(row.StartAttempted&&!(row.ShutdownVerified||status.Kind==ChargingWorkflowKind.Charge&&status.ShutdownVerified))
            return "原车桩停充核验缺失，不能凭人工移动或继电器应答释放资源。";
        return null;
    }
}
