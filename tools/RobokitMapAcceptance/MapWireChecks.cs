using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text.Json;
using System.Reflection;
using AgvDispatch.Connectors.Agv;

internal static class MapWireChecks
{
    public static async Task Run(string output,Action<bool,string> check)
    {
        var source=Path.GetFullPath("docs/vendor/robokit/map-format-20261014/smap-master/maps/6.smap");var map=await File.ReadAllBytesAsync(source);
        using var audit=JsonDocument.Parse(await File.ReadAllTextAsync("docs/commissioning/evidence/20261014-robokit-document-examples/document-audit.json"));
        var traces=new List<object>();
        foreach(ushort type in new ushort[]{4010,4011,2025})
        {
            var request=type==4011?JsonSerializer.SerializeToUtf8Bytes(new{map_name="6"}):map;
            var response=type==4011?map:FindDocumentReply(audit.RootElement,type);
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));var ct=timeout.Token;
            var mock=Task.Run(async()=>
            {
                using var connection=await listener.AcceptTcpClientAsync(ct);var stream=connection.GetStream();var h=new byte[16];await stream.ReadExactlyAsync(h,ct);
                if(h[0]!=0x5A || h[1]!=1 || BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(8))!=type || BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(4))!=request.Length)throw new Exception("模拟端报文头校验失败");
                var body=new byte[request.Length];await stream.ReadExactlyAsync(body,ct);if(!body.SequenceEqual(request))throw new Exception("完整原图正文改变");
                var rh=new byte[16];rh[0]=0x5A;rh[1]=1;h.AsSpan(2,2).CopyTo(rh.AsSpan(2));BinaryPrimitives.WriteInt32BigEndian(rh.AsSpan(4),response.Length);BinaryPrimitives.WriteUInt16BigEndian(rh.AsSpan(8),(ushort)(type+10000));
                await stream.WriteAsync(rh.AsMemory(0,7),ct);await stream.WriteAsync(rh.AsMemory(7),ct);await stream.WriteAsync(response,ct);
            },ct);
            using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,port,ct);var s=client.GetStream();
            var header=SeerProtocolCodec.CreateHeader(1,17,request.Length,type);await s.WriteAsync(header,ct);await s.WriteAsync(request,ct);
            var replyHeader=new byte[16];await s.ReadExactlyAsync(replyHeader,ct);var parsed=SeerProtocolCodec.ParseHeader(replyHeader);
            check(parsed.MessageType==type+10000 && parsed.Sequence==17 && parsed.PayloadLength==response.Length,$"API{type}生产Codec头部/应答编号/长度匹配");
            var replyBody=new byte[parsed.PayloadLength];await s.ReadExactlyAsync(replyBody,ct);await mock;
            check(replyBody.SequenceEqual(response),$"API{type}本机分块响应完整保留");
            using var json=JsonDocument.Parse(replyBody);
            check(type==4011?json.RootElement.TryGetProperty("header",out _)&&!json.RootElement.TryGetProperty("ret_code",out _):json.RootElement.GetProperty("ret_code").GetInt32()==0,type==4011?"4011成功返回完整地图，无需错误码包装":"API"+type+"文档成功响应JSON核对");
            traces.Add(new{Api=type,ResponseApi=type+10000,LoopbackPort=port,RequestBytes=request.Length,ResponseBytes=response.Length,RequestHeader=Convert.ToHexString(header),ResponseHeader=Convert.ToHexString(replyHeader),MapSource=source,Origin="local_mock_with_official_example_not_physical",Scope="test-only raw TCP framing; not production map upload client",ResponseSource=type==4011?"official sample file":"local archived API response example"});
        }
        await using var production=new SeerRobotClient(new(){VehicleId="NO-MAP-UPLOAD",IpAddress="127.0.0.1",Port=1,NavigationPort=1},new());
        var invoke=typeof(SeerRobotClient).GetMethod("RequestAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        foreach(ushort type in new ushort[]{4010,4011,2025})
        {
            try{using var doc=await (Task<JsonDocument>)invoke.Invoke(production,new object?[]{type,null,CancellationToken.None})!;check(false,"未实现类别被错误发送");}
            catch(NotSupportedException){check(true,$"当前生产客户端未配置API{type}通道，明确不冒称软件支持上传/切图");}
        }
        await File.WriteAllTextAsync(Path.Combine(output,"map-protocol-traces.json"),JsonSerializer.Serialize(traces,new JsonSerializerOptions{WriteIndented=true}));
    }
    static byte[] FindDocumentReply(JsonElement audit,ushort type)
    {
        foreach(var page in audit.GetProperty("pages").EnumerateArray())if(page.GetProperty("requestNumbers").EnumerateArray().Any(n=>n.GetInt32()==type))
            foreach(var b in page.GetProperty("jsonExamples").EnumerateArray())if(b.GetProperty("role").GetString()=="res" && b.GetProperty("payload").TryGetProperty("ret_code",out var r) && r.GetInt32()==0)return JsonSerializer.SerializeToUtf8Bytes(b.GetProperty("payload"));
        throw new InvalidDataException("缺少API"+type+"原文完整成功响应");
    }
}
