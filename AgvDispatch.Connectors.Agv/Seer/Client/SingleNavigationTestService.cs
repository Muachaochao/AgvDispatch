using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed class SingleNavigationTestOptions
{
    public bool Enabled { get; init; }
    public string VehicleId { get; init; } = "";
    public bool BusinessExecutionDisabled { get; init; }
    public int OtherApiPort { get; init; } = 19210;
    public bool ForkEnabled { get; init; }
    public string ForkModelMd5 { get; init; } = "";
    public string ForkRobotId { get; init; } = "";
    public int DetourWaitSeconds { get; init; } = 5;
    public bool AutoDetourEnabled { get; init; }
    public int AutoDetourWaitSeconds { get; init; } = 10;
}

/// <summary>Explicitly confirmed single-vehicle navigation; controlled replacements use a verified
/// 3066 station chain. Never retries a motion command.
/// Independent of PLC business tasks; uses fresh robot-world coordinates, never editor coordinates.</summary>
public sealed partial class SingleNavigationTestService : ISingleNavigationTestService
{
    private readonly SingleNavigationTestOptions _settings;
    private readonly AgvOptions _options;
    private readonly AgvConnectionManager _connections;
    private readonly IActiveTaskLookup _tasks;
    private readonly IMapExecutionGate _execution;
    private readonly string _path;
    private readonly IChargingDispatchGuard? _charging;
    private readonly INavigationTestDetourPlanner? _detourPlanner;
    private readonly IRoadClosureService? _roadClosures;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private NavigationTestPreview? _preview;
    private NavigationTestRecord? _record;
    private NavigationTestDetourPreview? _detourPreview;
    private static bool Terminal(string? state) => state is "Completed" or "Failed" or "Cancelled";

    public SingleNavigationTestService(SingleNavigationTestOptions settings, AgvOptions options,
        AgvConnectionManager connections, IActiveTaskLookup tasks, IMapExecutionGate execution, string path, IChargingDispatchGuard? charging=null,
        INavigationTestDetourPlanner? detourPlanner=null, IRoadClosureService? roadClosures=null)
    {
        _settings=settings; _options=options; _connections=connections; _tasks=tasks; _execution=execution; _path=path; _charging=charging; _detourPlanner=detourPlanner; _roadClosures=roadClosures;
        LoadJackRecord();
        LoadForkRecord();
        if(File.Exists(path))
        {
            _record=JsonSerializer.Deserialize<NavigationTestRecord>(File.ReadAllText(path))
                ?? throw new InvalidDataException("单次导航记录损坏，拒绝启动。");
            if(!Terminal(_record.State))
            {
                _record=_record with {State="Uncertain",BlockedSince=null,Message="后台重启：只读查询原任务，不自动重发。"};
                if(_record.Detour is {SendAttemptedAt:null} detour)
                    _record=_record with {State="ReviewHold",Detour=detour with {State="ReviewHold",StoppedObservedAt=null,Message="后台重启：未发送改道进入人工复核，不自动续发取消或导航。"}};
                SynchronizeSentDetourStatus();
                if(_record.State=="ReviewHold"&&_record.AutoDetour is {} auto)
                    _record=_record with {AutoDetour=auto with {State="ReviewHold",Message=_record.Message,UpdatedAt=DateTimeOffset.UtcNow}};
            }
        }
    }

    private void Authorize(TrafficUserContext user)
    {
        if(!user.Has(TrafficPermission.Administer)) throw new UnauthorizedAccessException("需要现场操作权限。");
    }
    private async Task<SeerRobotClient> GuardAsync(string vehicle, CancellationToken ct)
    {
        if (_charging?.OwnsVehicle(vehicle) == true)
            throw new InvalidOperationException("车辆仍有未闭合充电会话，不能执行独立导航或升降测试。");
        if(!_settings.Enabled || !_settings.BusinessExecutionDisabled || !_options.AllowCommands)
            throw new InvalidOperationException("单次导航或AGV动作未启用，或业务执行未关闭；不能混用自动调度。");
        if(string.IsNullOrWhiteSpace(_settings.VehicleId) || vehicle!=_settings.VehicleId)
            throw new InvalidOperationException("只能操作后台明确指定的单台测试车。");
        if(!_options.Enabled || _options.GetConfigurationError() is { } || !_connections.TryGet(vehicle,out var client))
            throw new InvalidOperationException("测试车通信配置不可用。");
        if((await _tasks.GetActiveTasksAsync(ct)).Any(task =>
            string.Equals(task.VehicleId,vehicle,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("测试车仍有未结束业务任务，不能进行独立导航测试。");
        return client;
    }
    public async Task ValidateRetainedBusinessRunAsync(string retainedVehicleId,CancellationToken ct)
    {
        if(!_settings.Enabled||!_settings.ForkEnabled||!_settings.BusinessExecutionDisabled)
            throw new InvalidOperationException("仅在已核定的叉车独立测试入口中，才能保留另一台车的业务责任。");
        if(string.IsNullOrWhiteSpace(retainedVehicleId)||
            string.Equals(retainedVehicleId,_settings.VehicleId,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原业务运行属于当前测试车，不能交叉测试。");
        if(!_options.Enabled||_options.GetConfigurationError() is { }||
            !_connections.TryGet(retainedVehicleId,out var retained))
            throw new InvalidOperationException("原业务车辆通信不可用，无法确认其当前状态。");
        var (telemetry,_)=await retained.PollAsync(ct);
        using var tasks=await retained.RequestAsync(1110,new{},ct);
        RequireIdleTaskList(tasks.RootElement);
        if(!Idle(telemetry))
            throw new InvalidOperationException("原业务车辆未确认静止、无报警且任务终态，暂不能开始叉车测试。");
    }
    private void RequireCommissioningMapRunning()
    {
        if(_execution.Mode!=MapExecutionMode.Running)
            throw new InvalidOperationException("地图切换尚未完成，单次调试必须等待运行地图处于运行状态。");
    }
    private static string? Text(JsonElement root,string key)=>root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
    private static int? Int(JsonElement root,string key)=>root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetInt32(out var n)?n:null;
    private static double? Number(JsonElement root,string key)=>root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetDouble(out var n)&&double.IsFinite(n)?n:null;
    private static bool Idle(AgvRobotTelemetry t)=>t.EmergencyStopped==false && t.Blocked==false &&
        !t.Alarms.Any(a=>a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal) &&
        t.VelocityX is { } vx && Math.Abs(vx)<0.01 && t.VelocityY is { } vy && Math.Abs(vy)<0.01 &&
        t.AngularVelocity is { } w && Math.Abs(w)<0.01 && t.Tasks.All(x=>x.Status is 4 or 5 or 6 or 7);

    private static void RequireIdleTaskList(JsonElement response)
    {
        // RBK 3.4 on the physical robot wraps 1110 in task_status_package.
        // Direct lists remain supported for other documented/fixture responses.
        var package=response;
        if(response.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("1110任务响应不是对象。");
        if(response.TryGetProperty("task_status_package",out var wrapped))
        {
            if(wrapped.ValueKind!=JsonValueKind.Object || response.TryGetProperty("task_status_list",out _))
                throw new InvalidDataException("1110任务列表包装错误或存在两份歧义列表。");
            package=wrapped;
        }
        if(!package.TryGetProperty("task_status_list",out var rows)||rows.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("1110未返回有效任务列表，请检查车端task_status_package.task_status_list。");
        foreach(var row in rows.EnumerateArray())
        {
            if(row.ValueKind!=JsonValueKind.Object || !row.TryGetProperty("status",out var rawStatus) ||
                rawStatus.ValueKind!=JsonValueKind.Number || !rawStatus.TryGetInt32(out var status) || status is <1 or >7)
                throw new InvalidDataException("1110含缺失或未知的任务状态，未放行导航。");
            // The real robot's completed Roboshop navigation can have an empty id.
            // Accept its terminal state only for the idle check, never as our own task receipt.
            if(status is 1 or 2 or 3)
                throw new InvalidOperationException($"车端仍有未结束任务：{Text(row,"task_id")??"编号未知"}，状态码{status}；请先由厂家结束原任务。");
        }
    }

    private async Task<NavigationTestPreview> ObserveAsync(NavigationTestQuery q,CancellationToken ct)
    {
        RequireNoInitialClosures();
        if(new[]{q.SourceSite,q.TargetSite}.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>128||s.Any(char.IsControl)) || q.SourceSite==q.TargetSite)
            throw new ArgumentException("请选择两个不同的厂家真实站点。");
        var client=await GuardAsync(q.VehicleId,ct);
        await RequireJackIdleForNavigationAsync(client,ct);
        await RequireForkIdleForNavigationAsync(client,ct);
        using var before=await client.RequestAsync(1300,null,ct);
        using var load=await client.RequestAsync(1022,null,ct);
        using var owner=await client.RequestAsync(1060,null,ct);
        if(!owner.RootElement.TryGetProperty("locked",out var locked)||locked.ValueKind!=JsonValueKind.False)
            throw new InvalidOperationException("控制权被占用或未知，请由厂家核实并释放；本入口不会抢占控制权。");
        var map=Text(before.RootElement,"current_map");var md5=Text(before.RootElement,"current_map_md5");
        if(string.IsNullOrWhiteSpace(map)||md5?.Length!=32||md5.Any(c=>!Uri.IsHexDigit(c))||Int(load.RootElement,"loadmap_status")!=1)
            throw new InvalidOperationException("当前车端地图身份缺失或地图尚未加载成功。");
        if((q.ExpectedRobotMap is not null||q.ExpectedRobotMapMd5 is not null)&&
            (q.ExpectedRobotMap!=map||q.ExpectedRobotMapMd5!=md5))
            throw new InvalidOperationException("车端地图与站点列表来源不一致，请重新读取车端站点。");
        using var stations=await client.RequestAsync(1301,null,ct);
        var rows=ParseStations(stations.RootElement);
        var source=rows.SingleOrDefault(x=>x.Id==q.SourceSite);
        var target=rows.SingleOrDefault(x=>x.Id==q.TargetSite);
        if(source is null||target is null)throw new InvalidOperationException("起点或终点不存在于当前车端地图。");
        using var route=await client.RequestAsync(1303,new{source_id=q.SourceSite,target_id=q.TargetSite},ct);
        var path=ParseRoute(route.RootElement,q,rows);
        var (telemetry,_)=await client.PollAsync(ct);
        using var taskList=await client.RequestAsync(1110,new{},ct);
        RequireIdleTaskList(taskList.RootElement);
        if(!Idle(telemetry)||telemetry.LocalizationConfidence is not >=0.5 || telemetry.Charging!=false ||
            telemetry.Alarms.Any(a=>a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal))
            throw new InvalidOperationException("车辆未确认静止、存在任务/报警、急停阻挡、充电或定位置信度不足。");
        var atSource=AtStation(telemetry,source.X,source.Y);
        if(!atSource)throw new InvalidOperationException("车辆不在所选起点；请先由厂家将车停到起点，再重新核验。");
        using var after=await client.RequestAsync(1300,null,ct);
        using var afterLoad=await client.RequestAsync(1022,null,ct);
        using var afterOwner=await client.RequestAsync(1060,null,ct);
        if(Text(after.RootElement,"current_map")!=map||Text(after.RootElement,"current_map_md5")!=md5||Int(afterLoad.RootElement,"loadmap_status")!=1||
            !afterOwner.RootElement.TryGetProperty("locked",out var al)||al.ValueKind!=JsonValueKind.False)
            throw new InvalidOperationException("核验期间地图或控制权发生变化，请重新核验。");
        return new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddSeconds(30),q.VehicleId,q.SourceSite,q.TargetSite,map,md5,path)
        {TargetX=target.X,TargetY=target.Y};
    }
    public async Task<NavigationTestPreview> PreviewAsync(TrafficUserContext user,NavigationTestQuery query,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            _preview=null;
            _detourPreview=null;
            if(_record is not null&&!Terminal(_record.State))throw new InvalidOperationException("原测试未结束或结果不确定，请先查询原任务。");
            using var lease=await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            return _preview=await ObserveAsync(query,ct);
        }
        finally{_mutex.Release();}
    }
    public async Task<NavigationTestRecord> SubmitAsync(TrafficUserContext user,NavigationTestSubmit request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            var preview=_preview;_preview=null; // one-use even when the send/response is interrupted
            if(!request.SiteClearConfirmed||preview is null||preview.PreviewId!=request.PreviewId||preview.ExpiresAt<DateTimeOffset.UtcNow)
                throw new InvalidOperationException("核验已失效或未确认测试区域，请重新核验。");
            if(_record is not null&&!Terminal(_record.State))throw new InvalidOperationException("原测试尚未结束。");
            using var lease=await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            var fresh=await ObserveAsync(new(preview.VehicleId,preview.SourceSite,preview.TargetSite),ct);
            if(fresh.RobotMap!=preview.RobotMap||fresh.RobotMapMd5!=preview.RobotMapMd5)throw new InvalidOperationException("地图身份已变化，拒绝沿用旧核验。");
            if(!fresh.Route.SequenceEqual(preview.Route,StringComparer.Ordinal)||fresh.TargetX!=preview.TargetX||fresh.TargetY!=preview.TargetY)
                throw new InvalidOperationException("车端规划路线或终点坐标已变化，请重新核验并确认。");
            if(DateTimeOffset.UtcNow>preview.ExpiresAt)throw new InvalidOperationException("发送前核验已过期。");
            var client=await GuardAsync(preview.VehicleId,ct);
            using var closureGate=_roadClosures is null?null:await _roadClosures.EnterRouteSendAsync(preview.Route,ct);
            RequireNoInitialClosures();
            var id="NAVTEST-"+Guid.NewGuid().ToString("N");
            var vendor=AgvTransportSegmentIds.For(id,2).Single();
            _record=new(id,vendor,preview.VehicleId,preview.SourceSite,preview.TargetSite,preview.RobotMap,preview.RobotMapMd5,"Uncertain","已记录发送意图；未确认车端接受，禁止自动重发。",DateTimeOffset.Now,user.WindowsIdentity)
            {NavigationApi=3051,Route=preview.Route.ToArray(),VendorTaskIds=[vendor],TargetX=preview.TargetX,TargetY=preview.TargetY};
            Persist(); // write intent durably before any motion bytes
            try
            {
                using var response=await client.RequestAsync(3051,new{source_id=preview.SourceSite,id=preview.TargetSite,task_id=vendor},ct);
                if(Int(response.RootElement,"ret_code")!=0)throw new InvalidDataException("导航应答缺少明确的ret_code=0。");
                _record=_record with{State="Accepted",Message="车端已应答；不代表到点完成。",UpdatedAt=DateTimeOffset.Now};
            }
            catch(Exception ex){_record=_record with{State="Uncertain",Message="下发结果不确定，禁止重发，请查询原任务或现场核对："+ex.Message,UpdatedAt=DateTimeOffset.Now};}
            Persist();return _record;
        }
        finally{_mutex.Release();}
    }
    public async Task<NavigationTestStatus> ReadAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if(_record is not null&&!Terminal(_record.State)&&_connections.TryGet(_record.VehicleId,out var client)&&_options.Enabled)
            {
                try
                {
                    if(_record.Detour is {SendAttemptedAt:null})
                    {
                        await ObserveDetourCancellationAsync(client,ct);
                        return BuildStatus();
                    }
                    var ids=CurrentVendorIds(_record);
                    var rows=await client.QueryTasksAsync(ids,ct);
                    var known=rows.Count==ids.Count&&rows.All(x=>x.Status is >=1 and <=7);
                    var state=!known?"Uncertain":rows.Any(x=>x.Status==2)?"Running":rows.Any(x=>x.Status==3)?"Suspended":rows.Any(x=>x.Status==1)?"Waiting":rows.Any(x=>x.Status is 5 or 7)?"Failed":rows.Any(x=>x.Status==6)?"Cancelled":"Completed";
                    var message=!known?"原任务链未全部找到或含未知状态，不视为未发送；保持阻塞。":$"原任务链状态 {string.Join(",",rows.Select(x=>$"{x.TaskId}={x.Status}"))}；仅全部终态且车辆静止时结束测试。";
                    if(known&&!Terminal(state)&&_record.CancellationAttemptedAt is not null)
                    {
                        state="CancelRequested";
                        message=$"已请求取消 {_record.CancellationVendorTaskId}，原任务链仍有活动分段：{string.Join(",",rows.Where(x=>x.Status is 1 or 2 or 3).Select(x=>$"{x.TaskId}={x.Status}"))}；取消尚未核实，只查询原编号，不重复取消或发送导航。";
                    }
                    var (t,_)=await client.PollAsync(ct);
                    ObservePosition(t);
                    if(Terminal(state))
                    {
                        using var taskList=await client.RequestAsync(1110,new{},ct);
                        RequireIdleTaskList(taskList.RootElement);
                        if(!Idle(t)){state="Uncertain";message="原任务已返回终态，但车辆尚未确认静止；请继续查询。";}
                        else if(state=="Completed")
                        {
                            var arrivalError=await CheckArrivalAsync(client,t,_record,ct);
                            if(arrivalError is not null){state="Uncertain";message=arrivalError;}
                            else message="原任务已完成，车端地图一致，车辆已在所选终点静止。";
                        }
                    }
                    _record=_record with{State=state,Message=message,UpdatedAt=DateTimeOffset.Now};
                    SynchronizeSentDetourStatus();
                    Persist();
                }
                catch(Exception ex)when(!ct.IsCancellationRequested)
                {
                    _record=_record with{State="Uncertain",BlockedSince=null,Message="查询失败，不能重发："+ex.Message,UpdatedAt=DateTimeOffset.Now};
                    if(_record.Detour is {SendAttemptedAt:null} pending)
                        _record=_record with {Detour=pending with {StoppedObservedAt=null,State=pending.State=="ReviewHold"?"ReviewHold":"AwaitingOriginalTerminal",Message=_record.Message}};
                    SynchronizeSentDetourStatus();
                    Persist();
                }
            }
            return BuildStatus();
        }
        finally{_mutex.Release();}
    }
    private NavigationTestStatus BuildStatus()
    {
            var enabled=_settings.Enabled&&_settings.BusinessExecutionDisabled&&_options.AllowCommands;
            var block=!enabled?"单次导航未启用、AGV动作未启用或业务执行未关闭。":
                !_options.Enabled||_options.GetConfigurationError() is not null||string.IsNullOrWhiteSpace(_settings.VehicleId)||
                    !_connections.TryGet(_settings.VehicleId,out _)?"测试车通信配置不可用。":
                _execution.Mode!=MapExecutionMode.Running?"地图切换尚未完成，单次调试暂不能发送。":null;
            if(block is null&&_jackRecord is not null&&!JackTerminal(_jackRecord.State))block="顶升测试未结束或结果不确定，请先查询顶升状态。";
            return new(enabled,_settings.VehicleId,_record,"单车路径导航，可经过中间站点；速度沿用厂家车端配置。不会启动PLC或自动调度。")
            {
                Ready=block is null,
                BlockReason=block,
                AutoDetourEnabled=_settings.AutoDetourEnabled,
                AutoDetourWaitSeconds=Math.Max(1,_settings.AutoDetourWaitSeconds)
            };
    }
    public async Task<NavigationTestRecord> CancelAsync(TrafficUserContext user,NavigationTestCancel request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            if(_record is null||_record.TaskId!=request.TaskId||Terminal(_record.State))throw new InvalidOperationException("没有匹配的未结束测试任务。");
            if(_record.Detour is {SendAttemptedAt:null})throw new InvalidOperationException("原任务正在受控改道核对中，不能另发取消。");
            if(_record.CancellationAttemptedAt is not null)throw new InvalidOperationException("当前任务已记录取消尝试，只能查询原编号，不能重复取消。");
            var client=await GuardAsync(_record.VehicleId,ct);
            var ids=CurrentVendorIds(_record);
            var rows=await client.QueryTasksAsync(ids,ct);
            if(rows.Count!=ids.Count||rows.Any(x=>x.Status is <1 or >7))throw new InvalidOperationException("任务链证据不完整，不能推测取消范围。");
            var task=ids.Select(id=>rows.Single(x=>x.TaskId==id)).FirstOrDefault(x=>x.Status is 1 or 2 or 3);
            if(task?.Status is not (1 or 2 or 3))throw new InvalidOperationException("原任务不是可取消状态；不能发送无范围取消。");
            _record=_record with{State="Uncertain",Message="已记录原任务取消意图；尚未确认取消结果。",CancellationAttemptedAt=DateTimeOffset.UtcNow,CancellationVendorTaskId=task.TaskId,UpdatedAt=DateTimeOffset.Now};Persist();
            try{await client.CancelNavigationAsync(task.TaskId,false,ct);_record=_record with{State="CancelRequested",Message="取消已应答，请继续查询至取消终态；不是急停。"};}
            catch(Exception ex){_record=_record with{Message="取消结果不确定，请查询原任务："+ex.Message};}
            SynchronizeSentDetourStatus();
            Persist();return _record;
        }
        finally{_mutex.Release();}
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var temp=_path+".tmp";
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(file,_record);file.Flush(true);}
        File.Move(temp,_path,true);
    }
    private void RequireNoInitialClosures()
    {
        if(_roadClosures is null)return;
        var snapshot=_roadClosures.GetSnapshot();
        if(!snapshot.Ready||snapshot.IdentityConflict||snapshot.Closures.Any(c=>c.Active&&c.MapId==snapshot.MapId&&c.VersionId==snapshot.VersionId))
            throw new InvalidOperationException("当前地图存在封锁或封路状态尚未就绪；普通3051可能自行选择封锁路段，禁止发送。请处理封路后重新核验，执行中的测试使用受控改道。");
    }
}
