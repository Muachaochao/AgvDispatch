using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Connectors.Agv;

/// <summary>Dedicated 4011 reader; no configuration/control write API is exposed.</summary>
public sealed class RobotMapDownloadService(AgvOptions options,AgvConnectionManager connections,int configurationPort=19207):IRobotMapDownloadService
{
    private readonly SemaphoreSlim _mutex=new(1,1);
    public async Task<RobotMapDownload> ReadCurrentAsync(RobotMapDownloadQuery query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query.VehicleId)||!options.Enabled||options.GetConfigurationError() is not null||!connections.TryGet(query.VehicleId,out var client))
            throw new InvalidOperationException("车辆未配置或采集未启用。");
        await _mutex.WaitAsync(ct);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(20));var token=timeout.Token;
            using var before=await client.RequestAsync(1300,null,token);
            using var load=await client.RequestAsync(1022,null,token);
            var map=Text(before.RootElement,"current_map");var md5=Text(before.RootElement,"current_map_md5");
            if(map is null||!Regex.IsMatch(map,@"^[0-9A-Za-z_-]{1,128}$")||md5?.Length!=32||md5.Any(c=>!Uri.IsHexDigit(c))||
                !load.RootElement.TryGetProperty("loadmap_status",out var state)||state.ValueKind!=JsonValueKind.Number||state.GetInt32()!=1)
                throw new InvalidOperationException("当前车端地图名称/MD5不可用，或地图尚未加载成功。");
            var downloadOptions=new AgvOptions{ProtocolVersion=options.ProtocolVersion,RequestIntervalMs=options.RequestIntervalMs,
                ConnectTimeoutMs=options.ConnectTimeoutMs,RequestTimeoutMs=15000,MaximumFrameBytes=Math.Min(options.MaximumFrameBytes,8*1024*1024)};
            await using var reader=new SeerTcpChannel(client.Robot.IpAddress,configurationPort,downloadOptions);
            using var content=await reader.RequestAsync(4011,new{map_name=map},token);
            if(content.RootElement.TryGetProperty("ret_code",out var code)&&code.ValueKind==JsonValueKind.Number&&code.GetInt32()!=0)
                throw new InvalidDataException($"4011下载失败：厂家返回码{code.GetInt32()}，{Text(content.RootElement,"err_msg")}");
            if(!content.RootElement.TryGetProperty("header",out var header)||Text(header,"mapType")!="2D-Map"||Text(header,"mapName")!=map)
                throw new InvalidDataException("4011未返回与当前地图同名的2D地图；可能是固件错误或不支持的地图格式。");
            using var after=await client.RequestAsync(1300,null,token);
            using var afterLoad=await client.RequestAsync(1022,null,token);
            if(Text(after.RootElement,"current_map")!=map||Text(after.RootElement,"current_map_md5")!=md5||
                !afterLoad.RootElement.TryGetProperty("loadmap_status",out var afterState)||afterState.GetInt32()!=1)
                throw new InvalidOperationException("下载期间车端地图身份或加载状态变化，已丢弃本次结果，请重新拉取。");
            var json=content.RootElement.GetRawText();var bytes=Encoding.UTF8.GetBytes(json);
            if(bytes.Length>8*1024*1024)throw new InvalidDataException("地图超过Web导入8MiB预算。");
            return new(query.VehicleId,map,md5!,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),json,DateTimeOffset.Now);
        }
        finally{_mutex.Release();}
    }
    private static string? Text(JsonElement element,string property)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
}
