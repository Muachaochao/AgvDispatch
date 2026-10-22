using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    static int checks;
    static void Check(bool condition,string text) { if(!condition) throw new Exception(text); checks++; Console.WriteLine("PASS "+text); }
    static async Task<int> Main()
    {
        try
        {
            foreach(var fault in new[]{Fault.CloseAfterAccept,Fault.SilentAfterAccept,Fault.ShortHeader,Fault.ShortBody,Fault.LateReply}) await Accepted(fault);
            await QueryFailure(); await SequenceFailure(); await OutboxRecovery();
            Console.WriteLine($"PASS: {checks} Robokit fault checks; loopback endpoints and isolated database only."); return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static AgvOptions Options(WireRobot robot)=>new(){Enabled=true,AllowCommands=true,RequestTimeoutMs=500,ConnectTimeoutMs=500,CommandConfirmationAttempts=2,CommandConfirmationDelayMs=50,Robots=[robot.Configuration]};
    static AgvStateCache Cache(AgvOptions options)
    {
        var cache=new AgvStateCache(options);
        cache.ApplyTelemetry("SIM",new AgvRobotTelemetry(0,0,0,1,"HOME",null,90,false,false,"Idle",null,null,null,[],[],DateTimeOffset.Now),TimeSpan.FromMilliseconds(1)); return cache;
    }
    static async Task Accepted(Fault fault)
    {
        await using var robot=new WireRobot(fault); var options=Options(robot); var cache=Cache(options);
        await using var manager=new AgvConnectionManager(options); var gateway=new SeerAgvGateway(cache,manager,options);
        var id="STEP-"+fault;
        var result=await gateway.CreateTransportTaskAsync(id,"SIM",["HOME","PICK"],new("JackLoad"),default);
        Check(result==id,"丢失/不完整响应后1110重新连接确认原编号 "+fault);
        Check(robot.CreateCount==1 && robot.ConnectionCount>=2,"确认重连不重复3066 "+fault);
        // The position can change after an accepted command. Idempotency lookup must precede origin checks.
        cache.ApplyTelemetry("SIM",new AgvRobotTelemetry(1,0,0,1,"PICK",null,90,false,false,"Idle",null,null,null,[],[],DateTimeOffset.Now),TimeSpan.FromMilliseconds(1));
        Check(await gateway.CreateTransportTaskAsync(id,"SIM",["HOME","PICK"],new("JackLoad"),default)==id && robot.CreateCount==1,"车辆离开起点后的重试复用已接收任务 "+fault);
        Check((await gateway.GetTransportTaskStatusAsync(id,default))?.State==AgvTransportTaskState.GoingToDestination,"连接恢复后可读原任务状态 "+fault);
        Check(robot.RequestIds.Where(x=>x.Type==1110).All(x=>x.Id==id),"所有确认查询都携带原步骤编号 "+fault);
    }
    static async Task QueryFailure()
    {
        await using var robot=new WireRobot(Fault.FirstQueryDisconnect); var options=Options(robot);
        await using var manager=new AgvConnectionManager(options); var gateway=new SeerAgvGateway(Cache(options),manager,options);
        Check(await gateway.CreateTransportTaskAsync("STEP-QUERY","SIM",["HOME","PICK"],new("Wait"),default)=="STEP-QUERY" && robot.CreateCount==1,
            "创建前1110断连只重试原编号只读查询，确认不存在后才创建一次");
        Check(await gateway.CreateTransportTaskAsync("STEP-QUERY","SIM",["HOME","PICK"],new("Wait"),default)=="STEP-QUERY" && robot.CreateCount==1,"重连后再次调用仍复用原任务，不重复创建");
    }
    static async Task SequenceFailure()
    {
        await using var robot=new WireRobot(Fault.WrongSequence); await using var client=new SeerRobotClient(robot.Configuration,Options(robot));
        var failed=false; try { await client.QueryTaskAsync("STEP-SEQUENCE",default); } catch(InvalidDataException) { failed=true; }
        Check(failed && !client.IsConnected,"错误响应序号拒绝且关闭连接");
        Check((await client.QueryTaskAsync("STEP-SEQUENCE",default))?.Status==404 && robot.ConnectionCount==2,"新连接不混用旧响应");
    }
    static async Task OutboxRecovery()
    {
        await using var robot=new WireRobot(Fault.ConfirmationUnavailable); var options=Options(robot);
        await using var manager=new AgvConnectionManager(options); var gateway=new SeerAgvGateway(Cache(options),manager,options);
        var directory=Path.Combine(Path.GetTempPath(),"agv-wire-fault-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); Console.WriteLine("Evidence: "+directory);
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(directory,"test.db")});
        var catalog=new TrafficResourceCatalog([new("NARROW",TrafficResourceType.NarrowAisle)]); await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        var tasks=new DispatchTaskRepository(db); var created=await tasks.TryCreateAsync("CNC-SIM","DUMPER-SIM",20,default); await tasks.MarkAcknowledgedAsync(created.Task.TaskId,default);
        var step=(await tasks.GetStepsAsync(created.Task.TaskId,default))[0]; var outbox=new AgvCommandOutboxRepository(db);
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(1)));
        await traffic.RecordRouteAsync(new(step.StepId,created.Task.TaskId,"SIM","HOME","PICK",["EDGE"],["NARROW"],false,"wire fault test"),default);
        Check((await traffic.TryAcquireAsync(step.StepId,created.Task.TaskId,"SIM",["NARROW"],default)).Acquired,"断连场景在发送前持有交通资源");
        Check(await outbox.TryPrepareTransportStepAsync(created.Task.TaskId,step.StepId,"SIM",["HOME","PICK"],new("JackLoad"),default),"发送前持久化稳定步骤与命令");
        var gate=new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
        var sender=new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,new(){RetryBaseDelayMs=1000,MaximumAttempts=3},outbox,gateway,gate);
        var dispatch=typeof(AgvCommandOutboxDispatcher).GetMethod("DispatchDueCommandsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(sender,[CancellationToken.None])!;
        var pending=await outbox.GetDueCommandsAsync(DateTimeOffset.Now.AddMinutes(1),10,default);
        Check(pending.Count==1 && pending[0].StepId==step.StepId && pending[0].AttemptCount==1,"确认查询均失败后原Outbox持久化为待重试");
        Check((await tasks.GetStepsAsync(created.Task.TaskId,default))[0].Status!=DispatchTaskStepStatus.Completed,"发送结果不确定不得完成运输步骤");
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==created.Task.TaskId),"发送结果不确定保留原路权");
        await db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l=>l.ExpiresAt==DateTime.Now.AddSeconds(-10)).Where(l=>l.TaskId==created.Task.TaskId).ExecuteCommandAsync();
        Check((await traffic.GetUnavailableResourceIdsAsync("OTHER",default)).Contains("NARROW"),"通信异常且心跳过期仍阻止其他车辆占用");
        var claimed=await outbox.TryClaimAsync(pending.Single().CommandId,DateTimeOffset.Now.AddMinutes(1),default);
        Check(claimed?.AttemptCount==2 && claimed.CommandId==pending[0].CommandId && claimed.StepId==step.StepId,"重试沿用原命令与原步骤编号");
        var send=typeof(AgvCommandOutboxDispatcher).GetMethod("SendAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)send.Invoke(sender,[claimed!,CancellationToken.None])!;
        var recovered=(await tasks.GetStepsAsync(created.Task.TaskId,default))[0];
        Check(recovered.Status==DispatchTaskStepStatus.Executing && recovered.VendorTaskId==step.StepId,"重试通过1110确认已接收后进入执行");
        Check(robot.CreateCount==1,"确认不可用跨Outbox重试仍只有一条3066");
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId==step.StepId),"确认成功只更新命令状态不提前释放交通资源");
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{TaskId=created.Task.TaskId,StepId=step.StepId,CommandId=claimed!.CommandId,robot.CreateCount,robot.ConnectionCount,Requests=robot.RequestIds},new JsonSerializerOptions{WriteIndented=true}));
    }
}
enum Fault { CloseAfterAccept,SilentAfterAccept,ShortHeader,ShortBody,LateReply,FirstQueryDisconnect,WrongSequence,ConfirmationUnavailable }
sealed class WireRobot : IAsyncDisposable
{
    public sealed record Request(ushort Type,string? Id);
    readonly TcpListener listener=new(IPAddress.Loopback,0); readonly CancellationTokenSource stop=new(); readonly Task server;
    readonly List<Task> clients=[]; readonly object sync=new(); string? accepted; bool injected; int blockedQueries;
    public List<Request> RequestIds { get; }=[];
    public int CreateCount { get; private set; } public int ConnectionCount { get; private set; }
    public AgvRobotOptions Configuration { get; }
    readonly Fault fault;
    public WireRobot(Fault fault) { this.fault=fault; listener.Start(); Configuration=new(){VehicleId="SIM",IpAddress="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port}; Configuration.NavigationPort=Configuration.Port; server=Task.Run(Accept); }
    async Task Accept()
    {
        try { while(!stop.IsCancellationRequested) { var client=await listener.AcceptTcpClientAsync(stop.Token); lock(sync) {ConnectionCount++; clients.Add(Handle(client));} } }
        catch(OperationCanceledException) { }
    }
    async Task Handle(TcpClient client)
    {
        using(client) using(var stream=client.GetStream())
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16]; await stream.ReadExactlyAsync(header,stop.Token);
                var length=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4)); if(length<0 || length>1048576) throw new Exception("Unexpected request length");
                var payload=new byte[length]; await stream.ReadExactlyAsync(payload,stop.Token); using var document=JsonDocument.Parse(payload);
                var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2)); string? id=null; bool disconnect=false,inject=false; object response;
                lock(sync)
                {
                    if(type==1110)
                    {
                        id=document.RootElement.GetProperty("task_ids")[0].GetString();
                        disconnect=(!injected && fault==Fault.FirstQueryDisconnect) || (accepted!=null && fault==Fault.ConfirmationUnavailable && blockedQueries++<2);
                        if(disconnect) injected=true;
                        if(fault==Fault.WrongSequence && !injected) {inject=true;injected=true;}
                        response=new {task_status_list=new[]{new {task_id=id,status=accepted==id?2:404}}};
                    }
                    else if(type==3066)
                    {
                        var moves=document.RootElement.GetProperty("move_task_list"); id=moves[moves.GetArrayLength()-1].GetProperty("task_id").GetString();
                        accepted=id; CreateCount++; inject=!injected; injected=true; response=new{ret_code=0};
                    }
                    else throw new Exception("Unexpected protocol request "+type);
                    RequestIds.Add(new(type,id));
                }
                if(disconnect || (inject && fault is Fault.CloseAfterAccept or Fault.ConfirmationUnavailable)) return;
                if(inject && fault==Fault.SilentAfterAccept) {await Task.Delay(Timeout.Infinite,stop.Token);return;}
                if(inject && fault==Fault.LateReply) await Task.Delay(900,stop.Token);
                var body=JsonSerializer.SerializeToUtf8Bytes(response); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),body.Length); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                if(inject && fault==Fault.WrongSequence) BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2,2),(ushort)(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2,2))+1));
                if(inject && fault==Fault.ShortHeader) {await stream.WriteAsync(header.AsMemory(0,7),stop.Token);return;}
                await stream.WriteAsync(header,stop.Token);
                if(inject && fault==Fault.ShortBody) {await stream.WriteAsync(body.AsMemory(0,Math.Max(1,body.Length/2)),stop.Token);return;}
                // Valid responses are split across writes; TCP packet boundaries are not frame boundaries.
                await stream.WriteAsync(body.AsMemory(0,1),stop.Token); await stream.WriteAsync(body.AsMemory(1),stop.Token); await stream.FlushAsync(stop.Token);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or EndOfStreamException or IOException or SocketException) { }
    }
    public async ValueTask DisposeAsync()
    { stop.Cancel(); listener.Stop(); await server; Task[] pending; lock(sync) pending=clients.ToArray(); try{await Task.WhenAll(pending);}finally{stop.Dispose();} }
}
