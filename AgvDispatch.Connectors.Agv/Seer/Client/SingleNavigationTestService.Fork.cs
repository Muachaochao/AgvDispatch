using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Connectors.Agv;

// Commissioned byDO pallet-truck profile only. Never infer height control from a model name.
public sealed partial class SingleNavigationTestService
{
    private ForkTestRecord? _forkRecord;
    private string ForkPath=>_path+".fork-"+Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(_settings.ForkRobotId))).ToLowerInvariant()[..12]+".json";
    private string LegacyForkPath=>_path+".fork.json";
    private static bool ForkTerminal(string state)=>state is "Completed" or "ObservedTarget" or "Stopped" or "Rejected";
    private void LoadForkRecord()
    {
        var path=File.Exists(ForkPath)?ForkPath:LegacyForkPath;
        if(!File.Exists(path))return;
        var record=JsonSerializer.Deserialize<ForkTestRecord>(File.ReadAllText(path))??throw new InvalidDataException("货叉记录损坏。");
        // The former shared filename cannot authorize a different vehicle. A
        // same-vehicle legacy record is retained; another vehicle's history is
        // left untouched and cannot block this vehicle's test.
        if(path==LegacyForkPath&&!string.Equals(record.VehicleId,_settings.ForkRobotId,StringComparison.Ordinal))return;
        _forkRecord=record;
        if(!Guid.TryParse(_forkRecord.RequestId,out _)||_forkRecord.Action is not ("Up" or "Down" or "Stop")||string.IsNullOrWhiteSpace(_forkRecord.VehicleId))throw new InvalidDataException("货叉记录字段无效。");
        if(!ForkTerminal(_forkRecord.State))_forkRecord=_forkRecord with{State="Uncertain",Message="后台重启：仅查询原动作反馈，不自动重发。"};
    }
    private void PersistFork()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ForkPath)!);
        var temp=ForkPath+".tmp";
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,_forkRecord);file.Flush(true);}
        File.Move(temp,ForkPath,true);
    }
    private void RequireForkProfile()
    {
        if(!_settings.ForkEnabled||!System.Text.RegularExpressions.Regex.IsMatch(_settings.ForkModelMd5,"^[a-fA-F0-9]{32}$")||string.IsNullOrWhiteSpace(_settings.ForkRobotId))
            throw new InvalidOperationException("未启用已核定的地牛货叉配置；禁止猜测升降参数。");
    }
    private async Task VerifyForkModelAsync(SeerRobotClient client,CancellationToken ct)
    {
        RequireForkProfile();
        using var info=await client.RequestAsync(1000,null,ct);
        if(Text(info.RootElement,"model_md5")!=_settings.ForkModelMd5||Text(info.RootElement,"vehicle_id")!=_settings.ForkRobotId)
            throw new InvalidOperationException("车辆身份或模型摘要已变化，必须重新核定货叉配置。");
        using var response=await client.RequestAsync(1500,null,ct);
        var devices=response.RootElement.GetProperty("deviceTypes").EnumerateArray()
            .Where(t=>Text(t,"name")=="fork").SelectMany(t=>t.GetProperty("devices").EnumerateArray())
            .Where(d=>RequiredBool(d,"isEnabled")).ToArray();
        if(devices.Length!=1)throw new InvalidDataException("没有唯一启用的货叉机构。");
        var type=devices[0].GetProperty("deviceParams").EnumerateArray().Single(p=>Text(p,"key")=="type").GetProperty("comboParam");
        if(Text(type,"childKey")!="byDO")throw new InvalidDataException("当前机构不是已核定的byDO地牛模式。");
        var parameters=type.GetProperty("childParams").EnumerateArray().Single(p=>Text(p,"key")=="byDO").GetProperty("params").EnumerateArray().ToArray();
        foreach(var (key,value) in new[]{("pumpDO",5),("leakDO",6),("topDI",6),("bottomDI",3)})
            if(Int(parameters.Single(p=>Text(p,"key")==key),"int32Value")!=value)throw new InvalidDataException("货叉IO映射已变化："+key);
    }
    private async Task<ForkTestTelemetry> FetchForkAsync(SeerRobotClient client,CancellationToken ct)
    {
        RequireForkProfile();
        using var response=await client.RequestAsync(1100,new{keys=new[]{"DI","DO","vx","vy","w","emergency","soft_emc","errors","vehicle_id","model_md5","task_status","charging"}},ct);
        var e=response.RootElement;
        if(Int(e,"ret_code") is {} code&&code!=0)throw new InvalidDataException("货叉状态查询失败："+code);
        if(Text(e,"vehicle_id")!=_settings.ForkRobotId||Text(e,"model_md5")!=_settings.ForkModelMd5)throw new InvalidDataException("货叉车辆或模型身份不匹配。");
        foreach(var key in new[]{"vx","vy","w"})if(Number(e,key) is not {} speed||Math.Abs(speed)>.001)throw new InvalidOperationException("车辆未确认静止，停止升降测试并现场核对。");
        if(RequiredBool(e,"emergency")||RequiredBool(e,"soft_emc")||RequiredBool(e,"charging"))throw new InvalidOperationException("车辆急停或充电，禁止升降。");
        if(!e.TryGetProperty("errors",out var errors)||errors.ValueKind!=JsonValueKind.Array||errors.GetArrayLength()!=0)throw new InvalidOperationException("车辆故障状态不明确或存在故障。");
        bool Signal(string group,int id,bool input)
        {
            var matches=e.GetProperty(group).EnumerateArray().Where(x=>Int(x,"id")==id&&Text(x,"source")=="normal").ToArray();
            if(matches.Length!=1||input&&!RequiredBool(matches[0],"valid"))throw new InvalidDataException($"{group}{id}反馈无效或不唯一。");
            return RequiredBool(matches[0],"status");
        }
        var t=new ForkTestTelemetry(Signal("DI",6,true),Signal("DI",3,true),Signal("DO",5,false),Signal("DO",6,false),AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow());
        if(t.UpperLimit&&t.LowerLimit||t.PumpOn&&t.ValveOn)throw new InvalidDataException("货叉上下限位或升降输出矛盾。");
        return t;
    }
    private void ReconcileFork(ForkTestTelemetry t)
    {
        if(_forkRecord is null||ForkTerminal(_forkRecord.State))return;
        if(_forkRecord.VehicleId!=_settings.VehicleId)throw new InvalidOperationException("货叉原记录属于另一台车。");
        var target=_forkRecord.Action=="Up"?t.UpperLimit:_forkRecord.Action=="Down"&&t.LowerLimit;
        if(!t.PumpOn&&!t.ValveOn&&(_forkRecord.Action=="Stop"||target))
            _forkRecord=_forkRecord with{State=_forkRecord.Action=="Stop"?"Stopped":_forkRecord.Acknowledged?"Completed":"ObservedTarget",Message=_forkRecord.Action=="Stop"?"升降输出已关闭；实际机构停止仍需现场观察。":"已核对目标限位有效且升降输出关闭。",UpdatedAt=t.ObservedAt};
        else if(AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()-_forkRecord.UpdatedAt>TimeSpan.FromSeconds(20))
            _forkRecord=_forkRecord with{State="Uncertain",Message="未在20秒内确认到位；不自动重发，请停止并现场核对。"};
        PersistFork();
    }
    private async Task RequireForkIdleForNavigationAsync(SeerRobotClient client,CancellationToken ct)
    {
        if(!_settings.ForkEnabled)return;
        var t=await FetchForkAsync(client,ct);ReconcileFork(t);
        if(t.PumpOn||t.ValveOn||!t.UpperLimit&&!t.LowerLimit||_forkRecord is not null&&!ForkTerminal(_forkRecord.State))
            throw new InvalidOperationException("货叉运动、停在中间或原动作未核实，不能导航；先查询、停止并下降到位。");
    }
    public async Task<ForkTestStatus> ReadForkAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if(!_settings.ForkEnabled)return new(false,false,null,_settings.VehicleId,null,null);
            ForkTestTelemetry? telemetry=null;string? block=null;
            try
            {
                RequireForkProfile();
                if(!_connections.TryGet(_settings.VehicleId,out var client))throw new InvalidOperationException("叉车通信未启用。");
                telemetry=await FetchForkAsync(client,ct);ReconcileFork(telemetry);
                await GuardAsync(_settings.VehicleId,ct);RequireCommissioningMapRunning();
                if(_record is not null&&!Terminal(_record.State))block="导航原任务未结束，禁止同时升降。";
                if(telemetry.PumpOn||telemetry.ValveOn||_forkRecord is not null&&!ForkTerminal(_forkRecord.State))block="原升降未结束或结果不确定；先查询或停止，不要重复发送。";
            }
            catch(Exception e)when(!ct.IsCancellationRequested){block=e.Message;}
            return new(true,block is null&&telemetry is not null,block,_settings.VehicleId,telemetry,_forkRecord);
        }
        finally{_mutex.Release();}
    }
    public async Task<ForkTestRecord> SubmitForkAsync(TrafficUserContext user,ForkTestSubmit request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            if(!Guid.TryParse(request.RequestId,out _)||request.Action is not ("Up" or "Down" or "Stop"))throw new ArgumentException("货叉请求编号或动作无效。");
            if(_forkRecord?.RequestId==request.RequestId)
            {
                if(_forkRecord.Action!=request.Action)throw new InvalidOperationException("原请求编号不能用于其他动作。");
                return _forkRecord;
            }
            if(_forkRecord?.RequestIds.Contains(request.RequestId)==true)throw new InvalidOperationException("这是历史动作请求，禁止再次发送。");
            RequireForkProfile();
            if(!_settings.Enabled||!_settings.BusinessExecutionDisabled||!_options.Enabled||!_options.AllowCommands||!_connections.TryGet(_settings.VehicleId,out var client))throw new InvalidOperationException("叉车独立测试模式或动作未启用。");
            if(_forkRecord is not null&&_forkRecord.VehicleId!=_settings.VehicleId)throw new InvalidOperationException("保留的货叉记录属于另一台车，须先核对原车。");
            // Stop remains possible if feedback or a new-work gate becomes unavailable.
            // It still requires explicit isolated mode, the same configured vehicle and an owned intent.
            if(request.Action=="Stop"&&_forkRecord is null)throw new InvalidOperationException("没有本入口待停止的升降动作；请查询原记录。");
            if(request.Action=="Stop")
            {
                using var info=await client.RequestAsync(1000,null,ct);
                if(Text(info.RootElement,"model_md5")!=_settings.ForkModelMd5||Text(info.RootElement,"vehicle_id")!=_settings.ForkRobotId)
                    throw new InvalidOperationException("停止前车辆身份不匹配，须现场处置。");
            }
            if(request.Action!="Stop")
            {
                if(!request.AreaClearConfirmed||!request.EmptyLoadConfirmed)throw new InvalidOperationException("须确认空载且升降范围安全。");
                await GuardAsync(_settings.VehicleId,ct);
                if(_record is not null&&!Terminal(_record.State))throw new InvalidOperationException("导航任务未结束。");
            }
            using var lease=await _execution.EnterControlAsync(ct);
            if(request.Action!="Stop")
            {
                RequireCommissioningMapRunning();await VerifyForkModelAsync(client,ct);
                using var owner=await client.RequestAsync(1060,null,ct);
                if(RequiredBool(owner.RootElement,"locked"))throw new InvalidOperationException("控制权已占用，本入口不会抢占。");
                using var tasks=await client.RequestAsync(1110,new{},ct);RequireIdleTaskList(tasks.RootElement);
                var t=await FetchForkAsync(client,ct);ReconcileFork(t);
                if(t.PumpOn||t.ValveOn||_forkRecord is not null&&!ForkTerminal(_forkRecord.State))throw new InvalidOperationException("升降原动作未结束；先停止并核对，禁止重发。");
                if(request.Action=="Up"&&t.UpperLimit||request.Action=="Down"&&t.LowerLimit)throw new InvalidOperationException("已在目标限位，无需重复动作。");
            }
            var ids=(_forkRecord?.RequestIds??[]).Append(request.RequestId).ToArray();
            _preview=null;
            _forkRecord=new(request.RequestId,_settings.VehicleId,request.Action,"Uncertain","已保存发送意图；应答不确定时只查询，不重发。",AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()){RequestIds=ids};PersistFork();
            try
            {
                await using var other=new SeerTcpChannel(client.Robot.IpAddress,_settings.OtherApiPort,_options);
                using var reply=await other.RequestAsync((ushort)(request.Action=="Stop"?6041:6040),request.Action=="Stop"?null:new{height=request.Action=="Up"?1:0},ct);
                var code=Int(reply.RootElement,"ret_code");
                _forkRecord=_forkRecord with{Acknowledged=code==0,State=code==0?"Accepted":code.HasValue?"Rejected":"Uncertain",Message=code==0?"车端已受理，等待限位及输出反馈；应答不代表到位。":code.HasValue?"车端拒绝："+code:"应答缺少返回码，结果不确定，禁止重发。"};PersistFork();
            }
            catch(Exception e){_forkRecord=_forkRecord with{State="Uncertain",Message="发送结果不确定，不重发；查询或停止并现场核对："+e.Message};PersistFork();}
            return _forkRecord;
        }
        finally{_mutex.Release();}
    }
}
