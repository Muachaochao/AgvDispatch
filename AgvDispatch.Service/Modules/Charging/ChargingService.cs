using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

/// <summary>Charging owns a vehicle until observed physical exit. Every write is
/// preceded by a durable intent; timeout, cancellation and restart never resend it.</summary>
public sealed partial class ChargingService : IsolatedBackgroundService, IChargingService
{
    private readonly ChargingOptions options;
    private readonly IChargingRuntimePort port;
    private readonly IChargingRobotControl robot;
    private readonly ChargingSessionStore store;
    private readonly ILogger<ChargingService> logger;
    private readonly TimeProvider clock;
    private readonly IChargingWorkflow? workflow;
    private readonly IChargerTelemetryMonitor? chargerMonitor;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private ChargingSession[] sessions;
    private DateTimeOffset Now => clock.GetUtcNow();

    public ChargingService(ChargingOptions options, IChargingRuntimePort port, IChargingRobotControl robot,
        string path, ILogger<ChargingService> logger, TimeProvider? clock = null, IChargingWorkflow? workflow = null,
        IChargerTelemetryMonitor? chargerMonitor = null)
    {
        this.options=options; this.port=port; this.robot=robot; this.logger=logger; this.clock=clock??TimeProvider.System;
        this.workflow=workflow; this.chargerMonitor=chargerMonitor;
        if(options.ConfigurationError() is {} error) throw new InvalidOperationException(error);
        store=new(path);
        sessions=store.Load();
        if(sessions.Any(s=>s.Phase is not (ChargingPhase.Completed or ChargingPhase.Queued or ChargingPhase.WaitingForBusiness or ChargingPhase.Hold)))
        {
            sessions=sessions.Select(s=>s.Phase is ChargingPhase.Completed or ChargingPhase.Queued or ChargingPhase.WaitingForBusiness or ChargingPhase.Hold ? s :
                s with {HeldPhase=s.Phase,Phase=ChargingPhase.Hold,UpdatedAt=Now,Message="后台重启：保留原车辆、充电位与路权；只读核对原命令，禁止自动重发。"}).ToArray();
            store.Save(sessions);
        }
    }

    public async ValueTask<IDisposable> EnterBusinessAsync(CancellationToken ct)
    { await mutex.WaitAsync(ct); return new MutexLease(mutex); }
    public bool HasActiveSessions => Volatile.Read(ref sessions).Any(s=>s.Phase!=ChargingPhase.Completed);
    public bool HasReservedSessions => Volatile.Read(ref sessions).Any(s=>s.HasReservation);
    public bool OwnsVehicle(string vehicleId) => Volatile.Read(ref sessions).Any(s=>s.Phase!=ChargingPhase.Completed&&s.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase));
    private sealed class MutexLease(SemaphoreSlim semaphore) : IDisposable
    { private SemaphoreSlim? value=semaphore; public void Dispose()=>Interlocked.Exchange(ref value,null)?.Release(); }
    public bool AllowsNewBusiness(string vehicleId)
    {
        if(Volatile.Read(ref sessions).Any(s=>s.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase)&&s.Phase!=ChargingPhase.Completed))return false;
        var vehicle=port.Vehicles.FirstOrDefault(v=>v.VehicleId.Equals(vehicleId,StringComparison.OrdinalIgnoreCase));
        return vehicle?.Charging!=true && !(vehicle?.BatteryPercent is {} battery && (battery<=options.CriticalBatteryPercent || options.Enabled && battery<=options.StartBatteryPercent));
    }
    public AgvTransportChain? FindChain(string commandId)
    {
        foreach(var row in Volatile.Read(ref sessions))
            if(row.Route is {} route)
            {
                if(commandId==row.SessionId+"-IN")return new(commandId,row.VehicleId,route.ToCharger,1,row.Phase==ChargingPhase.SendingToCharger);
                if(commandId==row.SessionId+"-OUT")return new(commandId,row.VehicleId,route.ExitPath,1,row.Phase==ChargingPhase.SendingExit);
                if(commandId==row.SessionId+"-RETURN" && route.ReturnPath.Length>=2)return new(commandId,row.VehicleId,route.ReturnPath,1,row.Phase==ChargingPhase.SendingReturn);
            }
        return null;
    }
    public async Task<ChargingStatus> ReadAsync(CancellationToken ct)
    {
        using var lease=await EnterBusinessAsync(ct);
        return new(options.Enabled,options,options.Enabled?(port.NewWorkBlockReason??(options.Sites.Any(s=>s.Commissioned)?null:"尚未配置经现场核定的充电位、允许车辆及安全退出路线。")):"自动充电未启用；严重低电量仍禁止新业务。",sessions.ToArray(),Now)
        { Chargers=chargerMonitor?.Read()??[] };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"自动充电周期失败，保留充电归属和路权。");}
            await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMs),clock,stoppingToken);
        }
    }
    private ChargingSession Save(ChargingSession row)
    {
        var next=sessions.Where(s=>s.SessionId!=row.SessionId).Append(row).ToArray();
        // Keep all active sessions and a bounded recent history.
        next=next.Where(s=>s.Phase!=ChargingPhase.Completed||s.RequestId is not null).Concat(next.Where(s=>s.Phase==ChargingPhase.Completed&&s.RequestId is null).OrderByDescending(s=>s.UpdatedAt).Take(200)).ToArray();
        store.Save(next); Volatile.Write(ref sessions,next); return row;
    }
    private ChargingSession Move(ChargingSession row,ChargingPhase phase,string message)=>Save(row with {Phase=phase,PhaseSince=Now,UpdatedAt=Now,Message=message});
    private ChargingSession Hold(ChargingSession row,string reason)=>Save(row with {HeldPhase=row.Phase==ChargingPhase.Hold?row.HeldPhase:row.Phase,Phase=ChargingPhase.Hold,UpdatedAt=Now,Message=reason});
    private bool Fresh(DateTimeOffset? stamp)=>stamp is {} t&&t<=Now.AddSeconds(1)&&Now-t<=TimeSpan.FromSeconds(options.StateMaxAgeSeconds);
    private bool TimedOut(ChargingSession row,int seconds)=>Now-row.PhaseSince>TimeSpan.FromSeconds(seconds);
    // Commissioned sites explicitly confirm positive current means charging;
    // normal negative discharge after relay-off is compatible with safe exit.
    private bool CurrentStopped(ChargingBatteryReading reading)=>reading.Charging==false&&reading.Current is {} current&&double.IsFinite(current)&&current<=options.MinimumChargingCurrent;

    public async Task RunCycleAsync(CancellationToken ct)
    {
        using var lease=await EnterBusinessAsync(ct);
        if(options.Enabled)
            foreach(var vehicle in port.Vehicles.Where(v=>v.BatteryPercent is >=0&&v.BatteryPercent<=options.StartBatteryPercent&&v.Connection==ConnectionState.Online&&Fresh(v.LastSuccessfulPollAt)))
                if(!sessions.Any(s=>s.Phase!=ChargingPhase.Completed&&s.VehicleId.Equals(vehicle.VehicleId,StringComparison.OrdinalIgnoreCase)))
                    Save(new("CHARGE-"+Guid.NewGuid().ToString("N"),vehicle.VehicleId,ChargingPhase.Queued,Now,Now,"低电量已进入充电队列；等待业务箱体循环及设备责任安全结束。") {TargetBatteryPercent=options.TargetBatteryPercent});

        foreach(var row in sessions.Where(s=>s.HasReservation).ToArray())
            await port.RenewAsync(row.SessionId,ct);
        using var execution=await port.EnterExecutionAsync(false,ct);
        if(execution is null)return;
        foreach(var current in sessions.Where(s=>s.Phase!=ChargingPhase.Completed)
            .OrderBy(s=>port.Vehicles.FirstOrDefault(v=>v.VehicleId==s.VehicleId)?.BatteryPercent??100).ThenBy(s=>s.CreatedAt).ToArray())
        {
            var row=current;
            try
            {
                if(row.Phase==ChargingPhase.Hold)continue;
                if(row.Phase is ChargingPhase.Queued or ChargingPhase.WaitingForBusiness)
                {
                    if(!options.Enabled || port.NewWorkBlockReason is not null)continue;
                    var busy=await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct);
                    if(busy is not null){Save(row with {Phase=ChargingPhase.WaitingForBusiness,Message=busy,UpdatedAt=Now});continue;}
                    var choices=new List<(ChargingSite Site,ChargingRoute Route)>();
                    foreach(var site in options.Sites.Where(s=>s.Commissioned&&CanHandleSite(s,row.VehicleId)&&s.AllowedVehicleIds.Contains(row.VehicleId,StringComparer.OrdinalIgnoreCase)&&
                        !sessions.Any(other=>other.HasReservation&&other.Site!.ResourceId.Equals(s.ResourceId,StringComparison.OrdinalIgnoreCase))))
                        if(await port.PlanWithReturnAsync(row.VehicleId,site,row.RequestedReturnSiteId,ct) is {} route)choices.Add((site,route));
                    foreach(var candidate in choices.OrderBy(c=>c.Route.Cost).ThenBy(c=>c.Site.Id,StringComparer.Ordinal))
                    {
                        row=Save(row with {Site=candidate.Site,Route=candidate.Route,Phase=ChargingPhase.Reserved,PhaseSince=Now,UpdatedAt=Now,Message="保存车辆、充电位和完整进出路线归属，申请独占路权。"});
                        if(await port.ReserveAsync(row,ct))break;
                        row=Save(row with {Site=null,Route=null,Phase=ChargingPhase.Queued,UpdatedAt=Now,Message="充电位或道路被占用，继续排队。"});
                    }
                    if(row.Phase!=ChargingPhase.Reserved)
                    {
                        // Keep the queue explainable: record why no charging bay could be planned instead of waiting silently.
                        if(choices.Count==0)
                        {
                            var reasons=options.Sites.Where(s=>s.Commissioned&&s.AllowedVehicleIds.Contains(row.VehicleId,StringComparer.OrdinalIgnoreCase))
                                .Select(s=>sessions.Any(o=>o.HasReservation&&o.Site!.ResourceId.Equals(s.ResourceId,StringComparison.OrdinalIgnoreCase))
                                    ?$"{s.Id}：已被其他充电会话预留"
                                    :!CanHandleSite(s,row.VehicleId)?$"{s.Id}：控制方式未就绪":$"{s.Id}：{port.LastPlanFailure(row.VehicleId,s)??"暂无可用路线"}")
                                .ToArray();
                            var message="排队中，暂无可用充电位："+(reasons.Length==0?"没有允许该车使用的已核定充电位。":string.Join("；",reasons));
                            if(row.Message!=message)Save(row with {Message=message,UpdatedAt=Now});
                        }
                        continue;
                    }
                }
                await AdvanceAsync(row,ct);
            }
            catch(Exception e)
            {
                // Read the latest durable intent, not the pre-send local variable.
                row=sessions.Single(s=>s.SessionId==row.SessionId);
                if(row.HasReservation)Hold(row,"充电流程暂停，保留充电位及路权："+e.Message);
                else Save(row with {Phase=ChargingPhase.WaitingForBusiness,Message="充电排队条件尚未满足："+e.Message,UpdatedAt=Now});
                logger.LogWarning(e,"充电流程保持待核对。SessionId={SessionId}",row.SessionId);
                if(ct.IsCancellationRequested)throw;
            }
        }
    }

    private async Task AdvanceAsync(ChargingSession row,CancellationToken ct)
    {
        if(row.Site?.ControlMode=="RobokitScript") {await AdvanceScriptAsync(row,ct);return;}
        ChargingBatteryReading? chargeReading=null;
        if(row.Phase is ChargingPhase.AwaitingCurrent or ChargingPhase.Charging or ChargingPhase.AwaitingStop)
        {
            // Thermal/current supervision precedes motion eligibility. A BMS fault
            // may disable navigation while this session's relay still needs off.
            chargeReading=await ReadBatteryAsync(row,ct);
            row=Save(row with {Battery=chargeReading,UpdatedAt=Now});
            if(chargeReading.ManualCharge==true){Hold(row,"检测到连接手动充电器信号，禁止驶离并保留原充电资源。");return;}
            if(chargeReading.Temperature is null||chargeReading.Temperature>=options.MaximumTemperature)
            {
                if(!row.StopAttempted)await StopAsync(row,"电池温度缺失或过温，停止充电并保留资源待人工核对。",ct,true);
                else Hold(row,"电池温度缺失或过温，停止结果须现场核对。");
                return;
            }
            if((row.Phase is ChargingPhase.AwaitingCurrent or ChargingPhase.Charging) && (row.StopRequested||chargeReading.BatteryPercent>=options.TargetBatteryPercent))
            {await StopAsync(row,row.StopRequested?"人工申请停止充电。":"达到目标电量，申请停止充电。",ct);return;}
            if(row.Phase==ChargingPhase.Charging&&
               (chargeReading.Charging!=true||chargeReading.Current is null||chargeReading.Current<=options.MinimumChargingCurrent||TimedOut(row,options.ChargingTimeoutSeconds)))
            {await StopAsync(row,"充电中无有效正电流或超过最长充电时间，保护停止并保留资源。",ct,true);return;}
            if(row.Phase==ChargingPhase.AwaitingCurrent&&TimedOut(row,options.CurrentTimeoutSeconds)&&
               (chargeReading.Charging!=true||chargeReading.Current is null||chargeReading.Current<=options.MinimumChargingCurrent))
            {await StopAsync(row,"对接后未建立有效充电电流，保护停止并保留资源。",ct,true);return;}
        }
        if(await port.ValidateAsync(row,row.Phase is ChargingPhase.Reserved or ChargingPhase.StartingCharge or ChargingPhase.AwaitingStop,ct) is {} error)
        {Hold(row,error);return;}
        if(row.Phase==ChargingPhase.Reserved)
        {
            if(port.NewWorkBlockReason is not null)return;
            if(await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct) is {} busy){Hold(row,busy);return;}
            await SendNavigationAsync(row,false,ct);return;
        }
        if(row.Phase is ChargingPhase.SendingToCharger or ChargingPhase.SendingExit)
        {Hold(row,"保留未确认的原导航发送意图，禁止自动重发。");return;}
        if(row.Phase is ChargingPhase.GoingToCharger or ChargingPhase.Exiting)
        {
            var exiting=row.Phase==ChargingPhase.Exiting;
            var status=await port.ObserveNavigationAsync(row.VendorTaskId??row.CommandId!,row.VehicleId,exiting?row.Route!.ExitPath:row.Route!.ToCharger,ct);
            if(status is null||status.VendorTaskId!=(row.VendorTaskId??row.CommandId)||status.VehicleId!=row.VehicleId||!Fresh(status.UpdatedAt)||status.State==AgvTransportTaskState.Unknown)
            {Hold(row,"原导航任务身份或完整回执不明，禁止推断完成及补发。");return;}
            if(status.State is AgvTransportTaskState.Cancelled or AgvTransportTaskState.Failed or AgvTransportTaskState.Suspended)
            {Hold(row,"车端导航被中止或人工干预："+status.State);return;}
            if(TimedOut(row,options.NavigationTimeoutSeconds)){Hold(row,"充电导航超时，保留充电位及路权。");return;}
            if(status.State!=AgvTransportTaskState.Completed)return;
            if(!port.IsAt(row.VehicleId,exiting?row.Site!.ExitSiteId:row.Site!.ChargingSiteId))return;
            if(exiting)
            {
                if(await port.ValidateAsync(row,true,ct) is {} stoppedError){Hold(row,stoppedError);return;}
                var battery=await ReadBatteryAsync(row,ct);
                if(!CurrentStopped(battery)||battery.ManualCharge==true||battery.AutoCharge==true)
                {Hold(row,"退出后仍在充电或停止电流证据不完整，保留充电位。");return;}
                await CompleteAsync(row with {Battery=battery},"充电已停止，原退出导航完成且实际离位；释放充电资源，恢复接单。",ct);return;
            }
            var released=await port.ShrinkToBayAsync(row,ct);
            row=Move(row,ChargingPhase.StartingCharge,"已完成原导航并核对真实充电点位置，准备车端充电继电器控制。"+
                (released>0?$"已释放来车、出口及未使用道路{released}项，充电期间仅保留充电位。":""));
        }
        if(row.Phase==ChargingPhase.StartingCharge)
        {
            if(row.StartAttempted){Hold(row,"原充电开启已尝试，禁止重复开启。");return;}
            if(!options.Enabled){Hold(row,"自动充电许可已关闭，保留现场状态待核对。");return;}
            if(await port.ValidateAsync(row,true,ct) is {} stopError||!port.IsAt(row.VehicleId,row.Site!.ChargingSiteId))
            {Hold(row,"充电开启前尚未确认停稳、实际到位及现场互锁。");return;}
            var reading=await ReadBatteryAsync(row,ct);
            if(reading.ManualCharge==true||reading.Charging==true){Hold(row,"检测到已连接手动充电器或已有充电，先核对归属。");return;}
            if(reading.Temperature is null||reading.Temperature>=options.MaximumTemperature){Hold(row,"电池温度缺失或超过充电上限。");return;}
            if(await port.ValidateAsync(row,true,ct) is {} latestError||!port.IsAt(row.VehicleId,row.Site!.ChargingSiteId))
            {Hold(row,"充电开启前最终停稳及现场互锁核对未通过。");return;}
            row=Save(row with {StartAttempted=true,Battery=reading,UpdatedAt=Now,Message="充电开启意图已持久化；6005应答不代表充电电流建立。"});
            await robot.SetChargingRelayAsync(row.VehicleId,true,ct);
            Move(row,ChargingPhase.AwaitingCurrent,"已收到继电器应答，等待充电状态和正电流共同确认。");return;
        }
        if(row.Phase is ChargingPhase.AwaitingCurrent or ChargingPhase.Charging or ChargingPhase.AwaitingStop)
        {
            var reading=chargeReading!;
            if(!port.IsAt(row.VehicleId,row.Site!.ChargingSiteId))
            {
                if(!row.StopAttempted)await StopAsync(row,"车辆未保持在原充电点停稳，保护停止充电并保留资源。",ct,true);
                else Hold(row,"车辆未保持在原充电点，保留资源等待现场核对。");
                return;
            }
            if(row.Phase==ChargingPhase.AwaitingStop)
            {
                if(CurrentStopped(reading))
                {
                    if(!port.IsAt(row.VehicleId,row.Site!.ChargingSiteId)){Hold(row,"停止后车辆不在原充电点，须核对人工移动。");return;}
                    // B07-FIX C8: wait (not Hold) for the exit road like any other vehicle; the bay stays reserved.
                    if(!await port.AcquireExitAsync(row,ct))
                    {
                        const string waiting="停充已确认，电流归零；出口路线被其他车辆占用，保留充电位并等待路权后驶离。";
                        if(row.Message!=waiting)Save(row with {Message=waiting,UpdatedAt=Now});
                        return;
                    }
                    await SendNavigationAsync(row,true,ct);return;
                }
                if(TimedOut(row,options.StopTimeoutSeconds))Hold(row,"停止充电后仍有电流或状态未知，禁止驶离及释放。");
                return;
            }
            if(row.Phase==ChargingPhase.AwaitingCurrent)
            {
                if(reading.Charging==true&&reading.Current>options.MinimumChargingCurrent)
                    Move(row,ChargingPhase.Charging,"充电状态及正电流已建立，保持充电至退出阈值。");
                return;
            }
        }
    }

    private async Task<ChargingBatteryReading> ReadBatteryAsync(ChargingSession row,CancellationToken ct)
    {
        var reading=await robot.ReadBatteryAsync(row.VehicleId,ct);
        if(reading.VehicleId!=row.VehicleId||!Fresh(reading.ObservedAt)||reading.BatteryPercent is not >=0 or >100 ||
           reading.Current is {} a&&!double.IsFinite(a)||reading.Temperature is {} t&&!double.IsFinite(t))
            throw new InvalidDataException("车端电池状态身份、时效或数值无效。");
        return reading;
    }
    private async Task SendNavigationAsync(ChargingSession row,bool exit,CancellationToken ct)
    {
        if(await port.ValidateAsync(row,true,ct) is {} error){Hold(row,error);return;}
        if(await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct) is {} busy){Hold(row,busy);return;}
        var commandId=row.SessionId+(exit?"-OUT":"-IN");
        row=Move(row with {CommandId=commandId,VendorTaskId=null},exit?ChargingPhase.SendingExit:ChargingPhase.SendingToCharger,"原导航身份和完整路径已持久化，开始一次发送。");
        var vendor=await port.NavigateAsync(commandId,row.VehicleId,exit?row.Route!.ExitPath:row.Route!.ToCharger,ct);
        if(vendor!=commandId)throw new InvalidDataException("充电导航回执身份与原请求不符。");
        Move(row with {VendorTaskId=vendor},exit?ChargingPhase.Exiting:ChargingPhase.GoingToCharger,exit?"停止已确认，执行原退出导航。":"前往已预留充电点，等待原任务完成及实际到位。");
    }
    private async Task StopAsync(ChargingSession row,string message,CancellationToken ct,bool holdAfter=false)
    {
        if(row.StopAttempted){Hold(row,"原停止充电已尝试，禁止自动重发；"+message);return;}
        row=Move(row with {StopAttempted=true,StopRequested=true},ChargingPhase.StoppingCharge,"停止充电意图已持久化；"+message);
        await robot.SetChargingRelayAsync(row.VehicleId,false,ct);
        if(holdAfter)Hold(row,message);else Move(row,ChargingPhase.AwaitingStop,"已收到停止继电器应答，等待停止状态和电流归零，尚未释放充电位。");
    }
    private async Task CompleteAsync(ChargingSession row,string message,CancellationToken ct)
    {
        // Release first. A crash before saving Completed remains conservatively held.
        await port.ReleaseAsync(row.SessionId,ct);
        Move(row,ChargingPhase.Completed,message);
    }

    public async Task<ChargingResult> RequestStopAsync(TrafficUserContext user,ChargingStopRequest request,CancellationToken ct)
    {
        if(!user.Has(TrafficPermission.Administer))throw new UnauthorizedAccessException("停止充电需要现场操作权限。");
        if(string.IsNullOrWhiteSpace(request.Reason))throw new ArgumentException("请填写停止原因。");
        using var lease=await EnterBusinessAsync(ct);
        var row=sessions.SingleOrDefault(s=>s.SessionId==request.SessionId)??throw new KeyNotFoundException("充电会话不存在。");
        if(row.Phase==ChargingPhase.Completed)return new(true,"充电会话已结束。");
        if(row.Phase is ChargingPhase.Queued or ChargingPhase.WaitingForBusiness)
        {Move(row with {Operator=user.WindowsIdentity,OperatorEvidence=request.Reason},ChargingPhase.Completed,"人工取消尚未取得资源或发送命令的充电需求。");return new(true,"已取消排队；低电量仍禁止新业务，下轮仍满足启动条件时会重新申请。");}
        if(row.Site?.ControlMode=="RobokitScript")
        {
            using var control=await port.EnterRecoveryControlAsync(ct);
            if(control is null)return new(false,"无法取得安全停止控制门，保留原状态待现场核对。");
            row=Save(row with {Operator=user.WindowsIdentity,OperatorEvidence=request.Reason});
            if(row.StopAttempted)return new(false,"原停止已申请，请查询原结果，不重复发送。");
            try
            {
                var stoppedPhase=row.Phase==ChargingPhase.Hold?row.HeldPhase:row.Phase;
                if(row.ScriptOperationId is not null&&stoppedPhase is not (ChargingPhase.SendingToCharger or ChargingPhase.GoingToCharger or ChargingPhase.SendingExit or ChargingPhase.Exiting or ChargingPhase.SendingReturn or ChargingPhase.Returning))
                    await StopScriptAsync(row,request.Reason,ct);
                else
                {
                    row=Save(row with {StopAttempted=true,StopRequested=true,UpdatedAt=Now});
                    await port.CancelNavigationAsync(row,ct);
                    Hold(row,"已针对唯一原导航申请取消，保留资源等待原任务终态和停车核验。");
                }
                return new(true,"停止申请已保存；只核对原任务及停充结果，充电位尚未释放。");
            }
            catch(Exception e){Hold(sessions.Single(s=>s.SessionId==row.SessionId),"原停止结果不确定，保留现场状态："+e.Message);return new(false,"停止结果不确定，不自动重复发送或释放资源。");}
        }
        if(row.StopAttempted)return new(false,"已有停止意图，禁止重复发送；请只读核对并处置原停止结果。");
        if(!row.StartAttempted)return new(false,"原导航尚未建立充电，不自动取消行驶或释放资源；请现场核对后处理保留会话。");
        // An explicit operator stop is a single relay-off safety action. The
        // control lease remains available during BootHold; it cannot send motion.
        using var execution=await port.EnterRecoveryControlAsync(ct);
        if(execution is null)return new(false,"无法取得安全停止控制门，保留原状态待现场核对。");
        try
        {
            Save(row=row with {Operator=user.WindowsIdentity,OperatorEvidence=request.Reason});
            await StopAsync(row,request.Reason,ct,true);
            return new(true,"安全停止已申请；保持待核对及充电位，现场核对后方可续接原停止观察或确认离位关闭。");
        }
        catch(Exception e){Hold(sessions.Single(s=>s.SessionId==row.SessionId),"停止结果不确定："+e.Message);return new(false,"停止结果不确定，禁止自动重发及释放资源。");}
    }

    public async Task<ChargingResult> ResolveHoldAsync(TrafficUserContext user,ChargingResolveRequest request,CancellationToken ct)
    {
        if(!user.Has(TrafficPermission.Administer))throw new UnauthorizedAccessException("充电恢复核对需要现场操作权限。");
        if(!Enum.IsDefined(request.Decision)||string.IsNullOrWhiteSpace(request.Reason)||!request.VehicleAndLoadConfirmed||!request.ChargerAndLocationConfirmed)
            return new(false,"请确认车辆载荷、充电桩与现场位置，并填写核对依据。");
        using var lease=await EnterBusinessAsync(ct);
        var row=sessions.SingleOrDefault(s=>s.SessionId==request.SessionId)??throw new KeyNotFoundException("充电会话不存在。");
        if(row.Phase!=ChargingPhase.Hold)return new(false,"仅可处理保持待核对的充电会话。");
        using var execution=request.Decision==ChargingHoldDecision.CloseAfterPhysicalExit
            ? await port.EnterRecoveryControlAsync(ct) : await port.EnterExecutionAsync(false,ct);
        if(execution is null)return new(false,"执行门禁尚未放行，保留充电归属。");
        var validation=request.Decision==ChargingHoldDecision.CloseAfterPhysicalExit
            ? await port.ValidateRecoveryCloseAsync(row,ct) : await port.ValidateAsync(row,false,ct);
        if(validation is {} error)return new(false,error);
        if(request.Decision!=ChargingHoldDecision.CloseAfterPhysicalExit&&await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct) is {} busy)return new(false,busy);
        row=row with {Operator=user.WindowsIdentity,OperatorEvidence=request.Reason,UpdatedAt=Now};
        if(request.Decision==ChargingHoldDecision.CloseAfterPhysicalExit)
        {
            if(row.Site?.ControlMode=="RobokitScript"&&await ScriptCloseBlockAsync(row,ct) is {} scriptBlock)return new(false,scriptBlock);
            var reading=await ReadBatteryAsync(row,ct);
            var closingSite=row.Site!.ControlMode=="RobokitScript"?row.Route!.ReturnSiteId??row.Site.ExitSiteId:row.Site.ExitSiteId;
            if(!port.IsAt(row.VehicleId,closingSite)||!CurrentStopped(reading)||reading.ManualCharge==true||reading.AutoCharge==true)
                return new(false,"实际退出位置、停稳、无充电及电流归零须共同确认，不能直接释放充电位。");
            await CompleteAsync(row with {Battery=reading},"现场核对车辆已停止充电并实际退出，关闭原会话并释放资源。依据："+request.Reason,ct);
            return new(true,"已按实际离位证据关闭充电会话并释放充电资源。");
        }
        var phase=row.HeldPhase;
        if(row.Site?.ControlMode=="RobokitScript"&&row.ScriptOperationId is not null&&
            phase is ChargingPhase.SendingDock or ChargingPhase.Docking or ChargingPhase.StartingSession or ChargingPhase.SessionRunning or ChargingPhase.SendingUndock or ChargingPhase.Undocking)
        {
            var script=await ObserveScriptAsync(row,ct);
            row=row with {WorkflowStatus=script,Battery=script.Battery??row.Battery};
            if(script.State==ChargingWorkflowState.Unknown)return new(false,"原脚本执行结果仍未知，不能重发、退车或释放资源。");
            if(row.StopRequested&&row.ScriptOperationKind==ChargingWorkflowKind.Charge&&script.ShutdownVerified&&script.State is ChargingWorkflowState.Succeeded or ChargingWorkflowState.Failed)
            {row=row with {ShutdownVerified=true};phase=ChargingPhase.AwaitingStop;}
            else if(script.State==ChargingWorkflowState.Failed)
            {
                return new(false,"原脚本失败，不能通过续接重做动作；请完成现场核对后关闭原会话。");
            }
            else if(row.StopRequested&&script.State==ChargingWorkflowState.Succeeded)row=row with {StopRequested=false};
        }
        if(phase is ChargingPhase.SendingToCharger)phase=ChargingPhase.GoingToCharger;
        if(phase is ChargingPhase.SendingExit)phase=ChargingPhase.Exiting;
        if(phase is ChargingPhase.SendingReturn)phase=ChargingPhase.Returning;
        if(phase is ChargingPhase.SendingDock)phase=ChargingPhase.Docking;
        if(phase is ChargingPhase.StartingSession)phase=ChargingPhase.SessionRunning;
        if(phase is ChargingPhase.SendingUndock)phase=ChargingPhase.Undocking;
        if(phase==ChargingPhase.StartingCharge&&row.StartAttempted)phase=ChargingPhase.AwaitingCurrent;
        if(phase==ChargingPhase.StoppingCharge)phase=ChargingPhase.AwaitingStop;
        if(phase is null or ChargingPhase.Hold or ChargingPhase.Completed)return new(false,"原阶段不可安全续接，请确认已停止充电并实际退出后关闭。");
        // Continuing an unknown write changes only observation state. No command is retried.
        if(phase is ChargingPhase.Reserved or ChargingPhase.StartingCharge && !options.Enabled)return new(false,"自动充电未启用，不能继续尚未发送的动作。");
        Move(row with {HeldPhase=null},phase.Value,"现场核对后续接原阶段；原命令只读查询，不重发。依据："+request.Reason);
        return new(true,"已续接原阶段；原充电位及路权继续保留。");
    }
}
