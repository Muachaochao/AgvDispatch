using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;

var evidence=Path.GetFullPath(args[0]);
using var audit=JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence,"document-audit.json")));
using var vectors=JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence,"vectors.json")));
var results=new List<object>();var failures=0;var framePass=0;var documentInvalid=0;var transportPass=0;var unsupported=0;
foreach(var page in audit.RootElement.GetProperty("pages").EnumerateArray())
foreach(var frame in page.GetProperty("rawFrames").EnumerateArray())
{
    var bytes=Convert.FromHexString(frame.GetProperty("hex").GetString()!);
    if(!frame.GetProperty("lengthMatches").GetBoolean())
    { documentInvalid++;results.Add(new{Kind="raw_frame",Page=page.GetProperty("path").GetString(),Line=frame.GetProperty("line").GetInt32(),Status="DOC_INVALID_LENGTH",Detail=$"declared={frame.GetProperty("declaredBytes")}, actual={frame.GetProperty("actualBytes")}"});continue; }
    try
    {
        var parsed=SeerProtocolCodec.ParseHeader(bytes.AsSpan(0,16));
        var generated=SeerProtocolCodec.CreateHeader(parsed.Version,parsed.Sequence,parsed.PayloadLength,parsed.MessageType);
        if(!generated.AsSpan(0,10).SequenceEqual(bytes.AsSpan(0,10)) || parsed.PayloadLength!=bytes.Length-16)
            throw new Exception("生产编解码器与文档前10字节或数据长度不一致。");
        framePass++;results.Add(new{Kind="raw_frame",Page=page.GetProperty("path").GetString(),Line=frame.GetProperty("line").GetInt32(),Status="PASS_HEADER_STRUCTURE",Type=parsed.MessageType,Detail="使用实际生产Codec；保留区及二进制负载不作JSON适配器支持证明"});
    }
    catch(Exception ex){failures++;results.Add(new{Kind="raw_frame",Page=page.GetProperty("path").GetString(),Status="FAIL_IMPLEMENTATION",Detail=ex.Message});}
}
var invoke=typeof(SeerRobotClient).GetMethod("RequestAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
foreach(var vector in vectors.RootElement.EnumerateArray())
{
    var type=vector.GetProperty("type").GetUInt16();
    var page=vector.GetProperty("page").GetString();
    var request=vector.GetProperty("request").GetProperty("payload").Clone();
    var response=vector.GetProperty("response").GetProperty("payload").Clone();
    var reqLine=vector.GetProperty("request").GetProperty("line").GetInt32();
    var resLine=vector.GetProperty("response").GetProperty("line").GetInt32();
    if(type is not (>=1000 and <2000) and not (>=3000 and <4000))
    {unsupported++;results.Add(new{Kind="request_response",Page=page,Type=type,RequestLine=reqLine,ResponseLine=resLine,Status="NOT_IMPLEMENTED_CHANNEL",Detail="项目未实现该类别通道；未连接设备"});continue;}
    using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    var port=((IPEndPoint)listener.LocalEndpoint).Port;
    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server=Serve(listener,type,request,response,budget.Token);
    await using var client=new SeerRobotClient(new(){VehicleId="DOC-LOOPBACK",IpAddress="127.0.0.1",Port=port,NavigationPort=port},
        new(){ProtocolVersion=1,ConnectTimeoutMs=1000,RequestTimeoutMs=1500});
    var expectsError=response.ValueKind==JsonValueKind.Object && response.TryGetProperty("ret_code",out var code) && code.ValueKind==JsonValueKind.Number && code.GetInt32()!=0;
    try
    {
        try
        {
            using var reply=await (Task<JsonDocument>)invoke.Invoke(client,new object?[]{type,request.ValueKind==JsonValueKind.Null?null:request,budget.Token})!;
            if(expectsError)throw new Exception("文档非零ret_code未被生产客户端拒绝。");
            if(!JsonElement.DeepEquals(reply.RootElement,response))throw new Exception("生产客户端返回JSON与文档示例不一致。");
        }
        catch(InvalidOperationException) when(expectsError) { }
        await server;
        transportPass++;results.Add(new{Kind="request_response",Page=page,Type=type,RequestLine=reqLine,ResponseLine=resLine,Status="PASS_TRANSPORT_EXAMPLE",Detail=expectsError?"实际客户端正确拒绝文档错误响应":"实际客户端收发JSON一致；不证明机器人业务或跨示例场景一致"});
    }
    catch(Exception ex){failures++;budget.Cancel();try{await server;}catch{}results.Add(new{Kind="request_response",Page=page,Type=type,RequestLine=reqLine,ResponseLine=resLine,Status="FAIL_IMPLEMENTATION",Detail=ex.Message});}
}
var businessPass=0;
// Keep document replies unchanged. Explicitly record request substitutions rather than
// pretending unrelated request/response examples describe one controller transaction.
foreach(var vector in vectors.RootElement.EnumerateArray().Where(v=>v.GetProperty("type").GetInt32()==1110))
{
    var response=vector.GetProperty("response").GetProperty("payload").Clone();
    var rows=response.GetProperty("task_status_package").GetProperty("task_status_list").EnumerateArray().ToArray();
    var ids=rows.Select(r=>r.GetProperty("task_id").GetString()!).ToArray();
    var source=$"{vector.GetProperty("page").GetString()}:{vector.GetProperty("response").GetProperty("line")}";
    await Business("1110批量任务原响应解析",source,1110,JsonSerializer.SerializeToElement(new{task_ids=ids}),response,async(c,ct)=>
    {
        var actual=await c.QueryTasksAsync(ids,ct);
        if(actual.Count!=rows.Length || actual.Where((r,i)=>r.TaskId!=ids[i] || r.Status!=rows[i].GetProperty("status").GetInt32() || r.Type!=rows[i].GetProperty("type").GetInt32()).Any())
            throw new Exception("任务编号/状态/类型未准确解析。");
    },"请求task_ids显式替换为响应实际编号；响应保持文档原值");
    foreach(var id in ids)
        await Business("1110单任务按原编号查找",source,1110,JsonSerializer.SerializeToElement(new{task_ids=new[]{id}}),response,async(c,ct)=>
        {
            var actual=await c.QueryTaskAsync(id,ct);
            var expected=rows.Single(r=>r.GetProperty("task_id").GetString()==id);
            if(actual?.TaskId!=id || actual.Status!=expected.GetProperty("status").GetInt32())throw new Exception("单任务解析不一致。");
        },"请求编号来自响应；单任务接口仅返回匹配记录，不证明厂家会附带额外记录");
    var original=vector.GetProperty("request").GetProperty("payload").Clone();
    var originalIds=original.GetProperty("task_ids").EnumerateArray().Select(x=>x.GetString()!).ToArray();
    await Business("1110原示例编号不一致时拒绝整链确认",source,1110,original,response,async(c,ct)=>
    {
        try{await c.QueryTasksAsync(originalIds,ct);}catch(InvalidDataException){return;}
        throw new Exception("响应额外编号未被拒绝。");
    },"请求与响应均原值；文档请求SEER78909与响应SEER78914/15不匹配，预期拒绝");
}
foreach(var smart in new[]{false,true})
{
    var vector=vectors.RootElement.EnumerateArray().First(v=>v.GetProperty("type").GetInt32()==3003);
    await Business("3003定向取消字段",vector.GetProperty("page").GetString()!,3003,
        JsonSerializer.SerializeToElement(new{smartCancel=smart,taskId="DOC-CANCEL-ONLY"}),vector.GetProperty("response").GetProperty("payload"),
        (c,ct)=>c.CancelNavigationAsync("DOC-CANCEL-ONLY",smart,ct),
        "按参数表构造带编号请求，区别于文档无参数取消示例；仅模拟收到ret_code=0，不证明车辆取消完成");
}
{
    var vector=vectors.RootElement.EnumerateArray().First(v=>v.GetProperty("type").GetInt32()==3066);
    var payload=vector.GetProperty("request").GetProperty("payload");
    var list=payload.GetProperty("move_task_list").EnumerateArray().ToArray();
    var path=new[]{list[0].GetProperty("source_id").GetString()!}.Concat(list.Select(r=>r.GetProperty("id").GetString()!)).ToArray();
    var root=list[^1].GetProperty("task_id").GetString()!;
    var expected=JsonSerializer.SerializeToElement(new{move_task_list=new object[]{
        new{source_id=path[0],id=path[1],task_id=root+"-01"},
        new{source_id=path[1],id=path[2],task_id=root,operation="JackHeight",jack_height=0.2}}});
    await Business("3066逐段路径与末段动作",vector.GetProperty("page").GetString()!,3066,expected,vector.GetProperty("response").GetProperty("payload"),
        (c,ct)=>c.GoToTargetAsync(root,path,new AgvTransportAction("JackHeight",0.2),true,ct),
        "使用文档LM1→LM2→AP1、JackHeight=0.2和响应；中间task_id按项目稳定编号策略替换为12344322-01，末段12344322不变");
}
var summary=new{FramePass=framePass,DocumentInvalidFrames=documentInvalid,TransportPass=transportPass,UnsupportedChannels=unsupported,BusinessPass=businessPass,ImplementationFailures=failures};
File.WriteAllText(Path.Combine(evidence,"example-test-results.json"),JsonSerializer.Serialize(new{Summary=summary,Results=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(summary));
Environment.ExitCode=failures==0?0:1;

async Task Business(string name,string source,ushort type,JsonElement request,JsonElement response,Func<SeerRobotClient,CancellationToken,Task> check,string transformation)
{
    using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    var port=((IPEndPoint)listener.LocalEndpoint).Port;
    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server=Serve(listener,type,request,response,budget.Token);
    await using var client=new SeerRobotClient(new(){VehicleId="DOC-BUSINESS",IpAddress="127.0.0.1",Port=port,NavigationPort=port},new(){ProtocolVersion=1,ConnectTimeoutMs=1000,RequestTimeoutMs=1500});
    try{await check(client,budget.Token);await server;businessPass++;results.Add(new{Kind="public_client",Name=name,Source=source,Type=type,Status="PASS_BUSINESS_ASSERTION",Detail=transformation});}
    catch(Exception ex){failures++;budget.Cancel();try{await server;}catch{}results.Add(new{Kind="public_client",Name=name,Source=source,Type=type,Status="FAIL_IMPLEMENTATION",Detail=transformation+"; "+ex.Message});}
}

static async Task Serve(TcpListener listener,ushort type,JsonElement request,JsonElement response,CancellationToken ct)
{
    using var socket=await listener.AcceptTcpClientAsync(ct);var stream=socket.GetStream();
    var header=new byte[16];await stream.ReadExactlyAsync(header,ct);
    var declared=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
    if(header[0]!=0x5A || header[1]!=1 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8))!=type || declared<0 || declared>4*1024*1024)
        throw new Exception("生产请求报文头不符。");
    var body=new byte[declared];await stream.ReadExactlyAsync(body,ct);
    if(request.ValueKind==JsonValueKind.Null){if(declared!=0)throw new Exception("无参数请求未发送零长度。");}
    else{using var parsed=JsonDocument.Parse(body);if(!JsonElement.DeepEquals(parsed.RootElement,request))throw new Exception("发包字段不等于文档请求示例。");}
    var output=JsonSerializer.SerializeToUtf8Bytes(response);
    // Independently frame the mock reply; do not use the codec under test to generate expected bytes.
    var reply=new byte[16];reply[0]=0x5A;reply[1]=1;header.AsSpan(2,2).CopyTo(reply.AsSpan(2));
    BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4),output.Length);BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8),(ushort)(type+10000));
    await stream.WriteAsync(reply.AsMemory(0,7),ct);await stream.WriteAsync(reply.AsMemory(7),ct);
    await stream.WriteAsync(output.AsMemory(0,Math.Min(1,output.Length)),ct);await stream.WriteAsync(output.AsMemory(Math.Min(1,output.Length)),ct);
}
