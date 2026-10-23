using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed class SingleNavigationTestOptions
{
    public bool Enabled { get; init; }
    public string VehicleId { get; init; } = "";
    public bool BusinessExecutionDisabled { get; init; }
}

/// <summary>One explicitly confirmed, adjacent vendor edge. Never retries a motion command.
/// Independent of PLC business tasks; uses fresh robot-world coordinates, never editor coordinates.</summary>
public sealed class SingleNavigationTestService : ISingleNavigationTestService
{
    private readonly SingleNavigationTestOptions _settings;
    private readonly AgvOptions _options;
    private readonly AgvConnectionManager _connections;
    private readonly IActiveTaskLookup _tasks;
    private readonly IMapExecutionGate _execution;
    private readonly string _path;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private NavigationTestPreview? _preview;
    private NavigationTestRecord? _record;
    private static bool Terminal(string? state) => state is "Completed" or "Failed" or "Cancelled";

    public SingleNavigationTestService(SingleNavigationTestOptions settings, AgvOptions options,
        AgvConnectionManager connections, IActiveTaskLookup tasks, IMapExecutionGate execution, string path)
    {
        _settings=settings; _options=options; _connections=connections; _tasks=tasks; _execution=execution; _path=path;
        if(File.Exists(path))
        {
            _record=JsonSerializer.Deserialize<NavigationTestRecord>(File.ReadAllText(path))
                ?? throw new InvalidDataException("单次导航记录损坏，拒绝启动。");
            if(!Terminal(_record.State)) _record=_record with {State="Uncertain",Message="后台重启：只读查询原任务，不自动重发。"};
        }
    }

    private void Authorize(TrafficUserContext user)
    {
        if(!user.Has(TrafficPermission.Administer)) throw new UnauthorizedAccessException("需要现场操作权限。");
    }
    private async Task<SeerRobotClient> GuardAsync(string vehicle, CancellationToken ct)
    {
        if(!_settings.Enabled || !_settings.BusinessExecutionDisabled || !_options.AllowCommands)
            throw new InvalidOperationException("单次导航或AGV动作未启用，或业务执行未关闭；不能混用自动调度。");
        if(string.IsNullOrWhiteSpace(_settings.VehicleId) || vehicle!=_settings.VehicleId)
            throw new InvalidOperationException("只能操作后台明确指定的单台测试车。");
        if(!_options.Enabled || _options.GetConfigurationError() is { } || !_connections.TryGet(vehicle,out var client))
            throw new InvalidOperationException("测试车通信配置不可用。");
        if((await _tasks.GetActiveTasksAsync(ct)).Count!=0)
            throw new InvalidOperationException("存在未结束业务任务，不能进行独立导航测试。");
        return client;
    }
    private static string? Text(JsonElement root,string key)=>root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
    private static int? Int(JsonElement root,string key)=>root.TryGetProperty(key,out var p)&&p.TryGetInt32(out var n)?n:null;
    private static double? Number(JsonElement root,string key)=>root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetDouble(out var n)&&double.IsFinite(n)?n:null;
    private static bool Idle(AgvRobotTelemetry t)=>t.EmergencyStopped==false && t.Blocked==false &&
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
        if(new[]{q.SourceSite,q.TargetSite}.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>128||s.Any(char.IsControl)) || q.SourceSite==q.TargetSite)
            throw new ArgumentException("请选择两个不同的厂家真实站点。");
        var client=await GuardAsync(q.VehicleId,ct);
        using var before=await client.RequestAsync(1300,null,ct);
        using var load=await client.RequestAsync(1022,null,ct);
        using var owner=await client.RequestAsync(1060,null,ct);
        if(!owner.RootElement.TryGetProperty("locked",out var locked)||locked.ValueKind!=JsonValueKind.False)
            throw new InvalidOperationException("控制权被占用或未知，请由厂家核实并释放；本入口不会抢占控制权。");
        var map=Text(before.RootElement,"current_map");var md5=Text(before.RootElement,"current_map_md5");
        if(string.IsNullOrWhiteSpace(map)||md5?.Length!=32||md5.Any(c=>!Uri.IsHexDigit(c))||Int(load.RootElement,"loadmap_status")!=1)
            throw new InvalidOperationException("当前车端地图身份缺失或地图尚未加载成功。");
        using var stations=await client.RequestAsync(1301,null,ct);
        if(!stations.RootElement.TryGetProperty("stations",out var list)||list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()>10000)
            throw new InvalidDataException("车端未返回有效站点列表。");
        var rows=list.EnumerateArray().ToArray();
        if(rows.Any(x=>x.ValueKind!=JsonValueKind.Object)||rows.GroupBy(x=>Text(x,"id"),StringComparer.Ordinal).Any(g=>string.IsNullOrWhiteSpace(g.Key)||g.Count()!=1))
            throw new InvalidDataException("车端站点编号缺失或重复。");
        var source=rows.Where(x=>Text(x,"id")==q.SourceSite).ToArray();
        if(source.Length!=1||rows.Count(x=>Text(x,"id")==q.TargetSite)!=1)throw new InvalidOperationException("起点或终点不存在于当前车端地图。");
        using var route=await client.RequestAsync(1303,new{source_id=q.SourceSite,target_id=q.TargetSite},ct);
        if(!route.RootElement.TryGetProperty("list",out var path)||path.ValueKind!=JsonValueKind.Array||path.GetArrayLength()!=2||
            path[0].ValueKind!=JsonValueKind.String||path[1].ValueKind!=JsonValueKind.String||path[0].GetString()!=q.SourceSite||path[1].GetString()!=q.TargetSite)
            throw new InvalidOperationException("当前入口只测试一条直接相连的路径；车端寻路结果必须恰好为所选起点和终点。");
        var (telemetry,_)=await client.PollAsync(ct);
        using var taskList=await client.RequestAsync(1110,new{},ct);
        RequireIdleTaskList(taskList.RootElement);
        if(!Idle(telemetry)||telemetry.LocalizationConfidence is not >=0.5 || telemetry.Charging!=false ||
            telemetry.Alarms.Any(a=>a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal))
            throw new InvalidOperationException("车辆未确认静止、存在任务/报警、急停阻挡、充电或定位置信度不足。");
        var sx=Number(source[0],"x");var sy=Number(source[0],"y");
        var atSource=sx is { } x && sy is { } y && telemetry.X is { } tx && telemetry.Y is { } ty && Math.Sqrt(Math.Pow(x-tx,2)+Math.Pow(y-ty,2))<=0.25;
        if(!atSource)throw new InvalidOperationException("车辆不在所选起点；请先由厂家将车停到起点，再重新核验。");
        using var after=await client.RequestAsync(1300,null,ct);
        using var afterLoad=await client.RequestAsync(1022,null,ct);
        using var afterOwner=await client.RequestAsync(1060,null,ct);
        if(Text(after.RootElement,"current_map")!=map||Text(after.RootElement,"current_map_md5")!=md5||Int(afterLoad.RootElement,"loadmap_status")!=1||
            !afterOwner.RootElement.TryGetProperty("locked",out var al)||al.ValueKind!=JsonValueKind.False)
            throw new InvalidOperationException("核验期间地图或控制权发生变化，请重新核验。");
        return new(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddSeconds(30),q.VehicleId,q.SourceSite,q.TargetSite,map,md5,[q.SourceSite,q.TargetSite]);
    }
    public async Task<NavigationTestPreview> PreviewAsync(TrafficUserContext user,NavigationTestQuery query,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            _preview=null;
            if(_record is not null&&!Terminal(_record.State))throw new InvalidOperationException("原测试未结束或结果不确定，请先查询原任务。");
            using var lease=await _execution.EnterAsync(MapExecutionOperation.NewWork,ct);
            if(lease is null)throw new InvalidOperationException("未发送导航："+_execution.NewWorkBlockReason);
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
            using var lease=await _execution.EnterAsync(MapExecutionOperation.NewWork,ct);
            if(lease is null)throw new InvalidOperationException("未发送导航："+_execution.NewWorkBlockReason+"；阻塞解除后请重新核验。");
            var fresh=await ObserveAsync(new(preview.VehicleId,preview.SourceSite,preview.TargetSite),ct);
            if(fresh.RobotMap!=preview.RobotMap||fresh.RobotMapMd5!=preview.RobotMapMd5)throw new InvalidOperationException("地图身份已变化，拒绝沿用旧核验。");
            if(DateTimeOffset.UtcNow>preview.ExpiresAt)throw new InvalidOperationException("发送前核验已过期。");
            var client=await GuardAsync(preview.VehicleId,ct);
            var id="NAVTEST-"+Guid.NewGuid().ToString("N");
            var vendor=AgvTransportSegmentIds.For(id,2).Single();
            _record=new(id,vendor,preview.VehicleId,preview.SourceSite,preview.TargetSite,preview.RobotMap,preview.RobotMapMd5,"Uncertain","已记录发送意图；未确认车端接受，禁止自动重发。",DateTimeOffset.Now,user.WindowsIdentity);
            Persist(); // write intent durably before any motion bytes
            try
            {
                using var response=await client.RequestAsync(3066,new{move_task_list=new[]{new{source_id=preview.SourceSite,id=preview.TargetSite,task_id=vendor}}},ct);
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
                    var task=(await client.QueryTasksAsync([_record.VendorTaskId],ct)).SingleOrDefault();
                    var state=task?.Status switch{1=>"Waiting",2=>"Running",3=>"Suspended",4=>"Completed",5 or 7=>"Failed",6=>"Cancelled",_=>"Uncertain"};
                    if(Terminal(state))
                    {
                        var (t,_)=await client.PollAsync(ct);
                        using var taskList=await client.RequestAsync(1110,new{},ct);
                        RequireIdleTaskList(taskList.RootElement);
                        if(!Idle(t))state="Uncertain";
                    }
                    _record=_record with{State=state,Message=task is null?"原任务未找到，不视为未发送；保持阻塞。":$"原任务状态码 {task.Status}；仅终态且车辆静止时结束测试。",UpdatedAt=DateTimeOffset.Now};Persist();
                }
                catch(Exception ex)when(!ct.IsCancellationRequested){_record=_record with{State="Uncertain",Message="查询失败，不能重发："+ex.Message,UpdatedAt=DateTimeOffset.Now};Persist();}
            }
            var enabled=_settings.Enabled&&_settings.BusinessExecutionDisabled&&_options.AllowCommands;
            var block=!enabled?"单次导航未启用、AGV动作未启用或业务执行未关闭。":
                !_options.Enabled||_options.GetConfigurationError() is not null?"测试车通信配置不可用。":
                _execution.NewWorkBlockReason;
            return new(enabled,_settings.VehicleId,_record,"现场单车、单段导航；速度沿用厂家车端路径配置。不会启动PLC或自动调度。")
            {Ready=block is null,BlockReason=block};
        }
        finally{_mutex.Release();}
    }
    public async Task<NavigationTestRecord> CancelAsync(TrafficUserContext user,NavigationTestCancel request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            if(_record is null||_record.TaskId!=request.TaskId||Terminal(_record.State))throw new InvalidOperationException("没有匹配的未结束测试任务。");
            var client=await GuardAsync(_record.VehicleId,ct);
            var task=(await client.QueryTasksAsync([_record.VendorTaskId],ct)).SingleOrDefault();
            if(task?.Status is not (1 or 2 or 3))throw new InvalidOperationException("原任务不是可取消状态；不能发送无范围取消。");
            _record=_record with{State="Uncertain",Message="已记录原任务取消意图；尚未确认取消结果。",UpdatedAt=DateTimeOffset.Now};Persist();
            try{await client.CancelNavigationAsync(_record.VendorTaskId,false,ct);_record=_record with{State="CancelRequested",Message="取消已应答，请继续查询至取消终态；不是急停。"};}
            catch(Exception ex){_record=_record with{Message="取消结果不确定，请查询原任务："+ex.Message};}
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
}
