using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace AgvDispatch.Connectors.Agv;

public sealed class RobokitMapTransferOptions
{
    public bool WritesEnabled { get; init; }
    public int ConfigurationPort { get; init; } = 19207;
    public int ControlPort { get; init; } = 19205;
    public string ConfirmedRobokitVersion { get; init; } = "";
    public string FirmwareConfirmationNote { get; init; } = "";
    public string Md5ConfirmationNote { get; init; } = "";
}
public sealed record RobokitMapTransferResult(string State,bool Acknowledged,bool DesiredMapObserved,
    string SourceSha256,string Message);

/// <summary>Explicit transfer kernel; not registered in production DI or exposed by IPC/UI.
/// Backend task/vehicle/road-right gates must be added before a production write entry is exposed.</summary>
public sealed class RobokitMapTransferSession : IAsyncDisposable
{
    private readonly SeerRobotClient _status;private readonly AgvOptions _agv;private readonly RobokitMapTransferOptions _settings;
    private readonly SeerTcpChannel _configuration,_control;private readonly SemaphoreSlim _gate=new(1);
    private bool _writeAttempted;
    public RobokitMapTransferSession(SeerRobotClient status,AgvOptions agv,RobokitMapTransferOptions settings)
    {
        _status=status;_agv=agv;_settings=settings;
        if(settings.ConfigurationPort is <1 or >65535 || settings.ControlPort is <1 or >65535)throw new ArgumentException("地图传输端口无效。");
        _configuration=new(status.Robot.IpAddress,settings.ConfigurationPort,agv);_control=new(status.Robot.IpAddress,settings.ControlPort,agv);
    }
    public async Task<RobokitMapTransferResult> TransferAsync(byte[] mapBytes,string expectedDeviceMd5,bool load,CancellationToken ct)
    {
        // Never auto-generate expected vehicle MD5 from local bytes: firmware reserialization rules are unconfirmed.
        var bytes=mapBytes.ToArray();var sha=Convert.ToHexString(SHA256.HashData(bytes));
        RobokitMapTransferResult Result(string state,bool ack,bool observed,string message)=>new(state,ack,observed,sha,message);
        if(!_agv.Enabled||!_agv.AllowCommands||!_settings.WritesEnabled)return Result("Blocked",false,false,"地图写入开关关闭，未连接/发送。");
        if(string.IsNullOrWhiteSpace(_settings.ConfirmedRobokitVersion)||string.IsNullOrWhiteSpace(_settings.FirmwareConfirmationNote)||string.IsNullOrWhiteSpace(_settings.Md5ConfirmationNote)||!Regex.IsMatch(expectedDeviceMd5,@"^[0-9a-fA-F]{32}$"))return Result("Blocked",false,false,"缺少固件及车端MD5规则核定，不发送。");
        if(bytes.Length==0||bytes.Length>_agv.MaximumFrameBytes)return Result("Blocked",false,false,"地图正文超过当前报文预算，不发送。");
        using var map=JsonDocument.Parse(bytes,new(){MaxDepth=64});var root=map.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("header",out var header)||header.ValueKind!=JsonValueKind.Object||!header.TryGetProperty("mapType",out var mapType)||mapType.ValueKind!=JsonValueKind.String||mapType.GetString()!="2D-Map"||!header.TryGetProperty("mapName",out var n)||n.ValueKind!=JsonValueKind.String)return Result("Blocked",false,false,"不是有效2D地图头。");
        if(JsonSerializer.SerializeToUtf8Bytes(root).Length>_agv.MaximumFrameBytes)return Result("Blocked",false,false,"序列化后的地图超过报文预算。");
        var name=n.GetString()!;if(!Regex.IsMatch(name,@"^[0-9a-zA-Z_-]{1,128}$"))return Result("Blocked",false,false,"厂家地图名不符合已留存格式。");
        await _gate.WaitAsync(ct);
        try
        {
            if(_writeAttempted)return Result("Blocked",false,false,"本会话已尝试地图写入，不自动补发；需要只读复核及外部处置。");
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));var token=budget.Token;
            using(var info=await _status.RequestAsync(1000,null,token))
                if(String(info.RootElement,"version")!=_settings.ConfirmedRobokitVersion)return Result("Blocked",false,false,"1000实际固件与核定版本不符。");
            using(var tasks=await _status.RequestAsync(1110,new{},token))
            {
                if(!tasks.RootElement.TryGetProperty("task_status_package",out var package)||!package.TryGetProperty("task_status_list",out var rows)||rows.ValueKind!=JsonValueKind.Array)return Result("Blocked",false,false,"1110任务状态缺失，不发送地图。");
                if(rows.EnumerateArray().Any(r=>r.ValueKind!=JsonValueKind.Object||!r.TryGetProperty("status",out var s)||s.ValueKind!=JsonValueKind.Number||!s.TryGetInt32(out var value)||value is not (4 or 5 or 6 or 7)))return Result("Blocked",false,false,"车辆存在未结束或未知任务，不发送地图。");
            }
            var acknowledged=false;var uncertain="";
            try
            {
                _writeAttempted=true;
                using var reply=await (load?_control:_configuration).RequestAsync(load?(ushort)2025:(ushort)4010,root,token);
                if(reply.RootElement.ValueKind!=JsonValueKind.Object||!reply.RootElement.TryGetProperty("ret_code",out var code)||code.ValueKind!=JsonValueKind.Number||!code.TryGetInt32(out var value))return Result("Uncertain",false,false,"写入响应缺明确ret_code，不重发。");
                if(value!=0)return Result("ApiRejected",false,false,"厂家写入返回错误码："+value);
                acknowledged=true;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)when(ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidDataException){uncertain=ex.Message;}
            // Exactly one write attempt. Loss/timeout proceeds only to read-only observation, never resends.
            try
            {
                using var before=await _status.RequestAsync(1300,null,token);
                using var beforeLoad=await _status.RequestAsync(1022,null,token);
                using var hashes=await _status.RequestAsync(1302,new{map_names=new[]{name+".smap"}},token);
                using var after=await _status.RequestAsync(1300,null,token);
                using var afterLoad=await _status.RequestAsync(1022,null,token);
                using var afterInfo=await _status.RequestAsync(1000,null,token);
                var list=hashes.RootElement.TryGetProperty("map_info",out var items)&&items.ValueKind==JsonValueKind.Array?items.EnumerateArray().Where(x=>String(x,"name")==name+".smap").ToArray():[];
                var md5Observed=list.Length==1&&String(list[0],"md5")==expectedDeviceMd5;
                var activeName=String(before.RootElement,"current_map");var activeMd5=String(before.RootElement,"current_map_md5");
                var stable=!string.IsNullOrWhiteSpace(activeName)&&activeMd5 is not null&&Regex.IsMatch(activeMd5,@"^[0-9a-fA-F]{32}$")&&activeName==String(after.RootElement,"current_map")&&activeMd5==String(after.RootElement,"current_map_md5")&&Loaded(beforeLoad.RootElement)&&Loaded(afterLoad.RootElement)&&String(afterInfo.RootElement,"version")==_settings.ConfirmedRobokitVersion;
                var desired=md5Observed&&stable&&(!load||(String(after.RootElement,"current_map")==name && String(after.RootElement,"current_map_md5")==expectedDeviceMd5));
                return Result(desired?"DesiredMapObserved":"Uncertain",acknowledged,desired,desired?"只读观察到预期地图/MD5；未自动绑定坐标、切换本地活动地图或放行任务。":"写入后地图/MD5/载入/固件未通过核验；不重发。"+uncertain);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){return Result("Uncertain",acknowledged,false,"写入后只读核验失败，不重发："+ex.Message);}
        }
        finally{_gate.Release();}
    }
    private static string? String(JsonElement root,string key)=>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
    private static bool Loaded(JsonElement root)=>root.TryGetProperty("loadmap_status",out var p)&&p.TryGetInt32(out var n)&&n==1;
    public async ValueTask DisposeAsync(){await _configuration.DisposeAsync();await _control.DisposeAsync();_gate.Dispose();}
}
