using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.WebHost;

// Private synthetic pipes only. No field pipe, database, PLC or robot is used.
await Task.WhenAll(DelayedFleetStart(),UnansweredRead());

static async Task DelayedFleetStart()
{
    var name="AgvDispatchStartSmoke-"+Guid.NewGuid().ToString("N");
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var accepted=0;
    var server=Task.Run(async()=>
    {
        await using var pipe=NewServer(name);
        var request=await Accept(pipe,stop.Token);
        if(request.Operation!="StartFleetTest"||request.Payload.GetProperty("requestId").GetString()!="original-start")
            throw new Exception("Original start identity changed.");
        Interlocked.Increment(ref accepted);
        await Task.Delay(TimeSpan.FromSeconds(26),stop.Token);
        var reply=JsonSerializer.SerializeToUtf8Bytes(new TrafficControlPipeResponse(true,null,
            JsonSerializer.SerializeToElement(new{requestId="original-start",accepted=true},WebJson.Options)),WebJson.Options);
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,reply.Length);
        await pipe.WriteAsync(header,stop.Token);await pipe.WriteAsync(reply,stop.Token);await pipe.FlushAsync(stop.Token);
    });
    try
    {
        var result=await new DispatchPipe(name).ReadAsync<JsonElement>("StartFleetTest",new{requestId="original-start"},stop.Token);
        if(!result.GetProperty("accepted").GetBoolean()||result.GetProperty("requestId").GetString()!="original-start"||accepted!=1)
            throw new Exception("Slow start did not preserve its single accepted original request.");
        await server;
        Console.WriteLine("PASS StartFleetTest retains the original request and receives its successful reply after 26 seconds, without resending.");
    }
    finally{await stop.CancelAsync();await server;}
}

static async Task UnansweredRead()
{
    var name="AgvDispatchReadSmoke-"+Guid.NewGuid().ToString("N");
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var received=0;
    var server=Task.Run(async()=>
    {
        await using var pipe=NewServer(name);
        var request=await Accept(pipe,stop.Token);
        if(request.Operation!="GetFleetTest")throw new Exception("Unexpected read operation.");
        Interlocked.Increment(ref received);
        try{await Task.Delay(Timeout.InfiniteTimeSpan,stop.Token);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    });
    try
    {
        await new DispatchPipe(name).ReadAsync<JsonElement>("GetFleetTest",new{},stop.Token);
        throw new Exception("Missing ordinary read timeout.");
    }
    catch(TimeoutException error)
    {
        if(!error.Message.Contains("回执超时")||!error.Message.Contains("处理结果尚未确认")||!error.Message.Contains("查询原记录")||received!=1)
            throw new Exception("Ordinary read did not preserve its bounded timeout and uncertainty message.",error);
        Console.WriteLine("PASS GetFleetTest still times out after its ordinary 25-second budget, reports an uncertain reply, and never resends.");
    }
    finally{await stop.CancelAsync();await server;}
}

static NamedPipeServerStream NewServer(string name)=>new(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
static async Task<TrafficControlPipeRequest> Accept(NamedPipeServerStream pipe,CancellationToken ct)
{
    await pipe.WaitForConnectionAsync(ct);
    var header=new byte[4];await pipe.ReadExactlyAsync(header,ct);
    var length=BinaryPrimitives.ReadInt32LittleEndian(header);
    if(length<=0||length>1024*1024)throw new Exception("Invalid test frame.");
    var body=new byte[length];await pipe.ReadExactlyAsync(body,ct);
    return JsonSerializer.Deserialize<TrafficControlPipeRequest>(body,WebJson.Options)??throw new Exception("Missing test request.");
}
