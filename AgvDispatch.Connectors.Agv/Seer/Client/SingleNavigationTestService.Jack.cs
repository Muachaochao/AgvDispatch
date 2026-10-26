using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
namespace AgvDispatch.Connectors.Agv;

public sealed partial class SingleNavigationTestService
{
    private JackTestRecord? _jackRecord;
    private string JackPath=>_path+".jack.json";
    private static bool JackTerminal(string state)=>state is "Completed" or "ObservedTarget" or "Rejected";
    private void LoadJackRecord()
    {
        if(!File.Exists(JackPath))return;
        _jackRecord=JsonSerializer.Deserialize<JackTestRecord>(File.ReadAllText(JackPath))??throw new InvalidDataException("顶升测试记录损坏，拒绝启动。");
        if(_jackRecord.Action is not ("Up" or "Down")||!Guid.TryParse(_jackRecord.RequestId,out _)||string.IsNullOrWhiteSpace(_jackRecord.VehicleId))throw new InvalidDataException("顶升测试记录字段无效。");
        if(!JackTerminal(_jackRecord.State))_jackRecord=_jackRecord with{State="Uncertain",Message="后台重启；仅查询顶升反馈，不重发。"};
    }
    private void PersistJack()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JackPath)!);var temp=JackPath+".tmp";
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,_jackRecord);file.Flush(true);}
        File.Move(temp,JackPath,true);
    }
    private static bool RequiredBool(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind is JsonValueKind.True or JsonValueKind.False?p.GetBoolean():throw new InvalidDataException("顶升状态缺少明确字段："+key);
    private static async Task<JackTestTelemetry> FetchJackAsync(SeerRobotClient client,CancellationToken ct)
    {
        using var response=await client.RequestAsync(1027,null,ct);var e=response.RootElement;
        if(Int(e,"ret_code") is {} code&&code!=0)throw new InvalidDataException("查询顶升失败，厂家返回码："+code);
        var state=Int(e,"jack_state")??throw new InvalidDataException("车端未提供顶升状态，不能测试。");
        if(state is not (0 or 1 or 2 or 3 or 4 or 255))throw new InvalidDataException("未知顶升状态码："+state);
        var error=Int(e,"jack_error_code")??throw new InvalidDataException("车端未提供顶升错误码。");
        double? height=e.TryGetProperty("jack_height",out var h)&&h.ValueKind==JsonValueKind.Number&&h.TryGetDouble(out var v)&&double.IsFinite(v)&&v>=0?v:null;
        return new(state,RequiredBool(e,"jack_enable"),RequiredBool(e,"jack_emc"),error,RequiredBool(e,"jack_isFull"),height,DateTimeOffset.Now);
    }
    private static string? JackHealth(JackTestTelemetry t)=>!t.Enabled?"车端顶升未启用或未配置。":t.Emergency?"顶升急停未解除。":t.ErrorCode!=0||t.State==255?"顶升故障，错误码："+t.ErrorCode:t.State is 0 or 2?"顶升机构正在运动，请等待反馈。":null;
    private void ReconcileJack(JackTestTelemetry telemetry)
    {
        if(_jackRecord is null||JackTerminal(_jackRecord.State))return;
        var target=_jackRecord.Action=="Up"?1:3;
        var state=telemetry.State==255||telemetry.ErrorCode!=0?"Uncertain":_jackRecord.State;
        var message=state=="Uncertain"?"顶升结果尚未确定；请查看状态并现场核对，禁止重发。":_jackRecord.Message;
        if(telemetry.Enabled&&!telemetry.Emergency&&telemetry.ErrorCode==0&&telemetry.State==target)
        {
            state=_jackRecord.Acknowledged?"Completed":"ObservedTarget";
            message=state=="ObservedTarget"?"应答曾不确定，现已观测到目标到位状态；此API无独立任务编号，未重发。":"已查询到顶升目标到位；不是仅凭发送应答判断完成。";
        }
        else if(telemetry.State is 0 or 2){state="Moving";message="已观测到顶升运动，继续查询至目标到位。";}
        _jackRecord=_jackRecord with{State=state,Message=message,UpdatedAt=telemetry.ObservedAt};PersistJack();
    }
    private async Task RequireJackIdleForNavigationAsync(SeerRobotClient client,CancellationToken ct)
    {
        if(_settings.ForkEnabled)return;
        if(_jackRecord is null)return;
        if(_jackRecord.VehicleId!=client.Robot.VehicleId)throw new InvalidOperationException("未结束顶升记录属于另一台车，不能混用。");
        var t=await FetchJackAsync(client,ct);ReconcileJack(t);
        if(!JackTerminal(_jackRecord!.State)||JackHealth(t) is not null)throw new InvalidOperationException("顶升尚未到位或状态异常，不能同时导航。");
    }
    public async Task<JackTestStatus> ReadJackAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            // Lifting is new work and is checked again on submit. Lowering is always visible
            // as a maintenance escape from a raised loaded state; its submit path uses only
            // the serialized map-control lease and still requires a fresh idle vehicle.
            var block=!_settings.Enabled||!_settings.BusinessExecutionDisabled||!_options.AllowCommands?"单车测试入口或AGV动作未启用。":null;
            var vehicleId=_jackRecord is not null&&!JackTerminal(_jackRecord.State)?_jackRecord.VehicleId:_settings.VehicleId;
            if(string.IsNullOrWhiteSpace(vehicleId)){var enabled=_options.Robots.Where(r=>r.Enabled).ToArray();if(enabled.Length==1)vehicleId=enabled[0].VehicleId;}
            JackTestTelemetry? telemetry=null;
            if(_options.Enabled&&_connections.TryGet(vehicleId,out var client))
            {
                try{telemetry=await FetchJackAsync(client,ct);ReconcileJack(telemetry);block??=JackHealth(telemetry);}
                catch(Exception e)when(!ct.IsCancellationRequested){block="顶升反馈不可用："+e.Message;}
            }
            else block??="指定测试车通信未启用。";
            if(_record is not null&&!Terminal(_record.State))block??="导航任务未结束，不能同时顶升。";
            if(_jackRecord is not null&&!JackTerminal(_jackRecord.State))block??="原顶升操作未结束或结果不确定，请查询状态，不能重复发送。";
            return new(block is null&&telemetry is not null,block,vehicleId,telemetry,_jackRecord);
        }
        finally{_mutex.Release();}
    }
    public async Task<JackTestRecord> SubmitJackAsync(TrafficUserContext user,JackTestSubmit request,CancellationToken ct)
    {
        if(_settings.ForkEnabled)throw new InvalidOperationException("地牛模式只允许货叉接口，禁止混用底盘顶升指令。");
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            if(!Guid.TryParse(request.RequestId,out _)||request.Action is not ("Up" or "Down"))throw new ArgumentException("顶升请求编号或动作无效。");
            if(_jackRecord?.RequestId==request.RequestId)
            {
                if(_jackRecord.Action!=request.Action)throw new InvalidOperationException("不能将原请求编号用于其他动作。");
                return _jackRecord;
            }
            if(_record is not null&&!Terminal(_record.State))throw new InvalidOperationException("原导航任务未结束。");
            var client=await GuardAsync(_settings.VehicleId,ct);
            // Jack actions are local commissioning operations, not transport work. Serialize
            // them with map control and keep the hardware/task safety checks below.
            using var lease=await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            if(_jackRecord is not null&&!JackTerminal(_jackRecord.State)&&_jackRecord.VehicleId!=_settings.VehicleId)throw new InvalidOperationException("未结束顶升记录属于另一台车。");
            var t=await FetchJackAsync(client,ct);ReconcileJack(t);
            if(_jackRecord is not null&&!JackTerminal(_jackRecord.State))throw new InvalidOperationException("原顶升测试未结束或结果不确定。");
            if(JackHealth(t) is {} health)throw new InvalidOperationException(health);
            if(t.State==(request.Action=="Up"?1:3))throw new InvalidOperationException("机构已经在所选目标位置，无需重复动作。");
            using var owner=await client.RequestAsync(1060,null,ct);
            if(!owner.RootElement.TryGetProperty("locked",out var locked)||locked.ValueKind!=JsonValueKind.False)throw new InvalidOperationException("控制权被占用或未知；本入口不会抢占控制权。");
            var (vehicle,_)=await client.PollAsync(ct);
            using var tasks=await client.RequestAsync(1110,new{},ct);RequireIdleTaskList(tasks.RootElement);
            if(!Idle(vehicle)||vehicle.Charging!=false||vehicle.LocalizationConfidence is not >=.5||vehicle.Alarms.Any(a=>a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal))throw new InvalidOperationException("车辆未确认静止、存在故障/任务/充电，不能顶升。");
            _preview=null;
            _jackRecord=new(request.RequestId,_settings.VehicleId,request.Action,"Uncertain","已持久化顶升发送意图；结果不确定时禁止重发。",t.State,DateTimeOffset.Now,user.WindowsIdentity);PersistJack();
            try
            {
                await using var other=new SeerTcpChannel(client.Robot.IpAddress,_settings.OtherApiPort,_options);
                using var response=await other.RequestAsync((ushort)(request.Action=="Up"?6070:6071),null,ct);
                var code=Int(response.RootElement,"ret_code");
                _jackRecord=_jackRecord with{Acknowledged=code==0,State=code==0?"Accepted":code is not null?"Rejected":"Uncertain",Message=code==0?"车端已应答，等待1027反馈到位。":code is not null?"车端拒绝动作，返回码："+code:"应答缺少明确返回码，结果不确定，禁止重发。",UpdatedAt=DateTimeOffset.Now};
            }
            catch(Exception ex){_jackRecord=_jackRecord with{State="Uncertain",Message="发送结果不确定，禁止重发；请查询反馈并现场核对："+ex.Message,UpdatedAt=DateTimeOffset.Now};}
            PersistJack();return _jackRecord;
        }
        finally{_mutex.Release();}
    }
}
