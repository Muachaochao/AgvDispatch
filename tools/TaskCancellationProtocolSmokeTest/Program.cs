using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;

internal static class Program
{
    static int checks;
    static void Check(bool condition,string text) { if(!condition) throw new Exception(text); checks++; Console.WriteLine("PASS "+text); }
    static async Task<int> Main()
    {
        try { foreach(var mode in Enum.GetValues<CancelMode>()) await Run(mode); Console.WriteLine($"PASS: {checks} cancellation protocol checks; loopback Robokit and isolated SQLite only."); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static async Task Run(CancelMode mode)
    {
        var root=Path.Combine(Path.GetTempPath(),"agv-cancel-protocol-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); Console.WriteLine("Evidence: "+root);
        await using var robot=new CancelRobot(mode);
        var options=new AgvOptions { Enabled=true,AllowCommands=true,RequestTimeoutMs=500,ConnectTimeoutMs=500,Robots=[robot.Configuration] };
        var cache=new AgvStateCache(options); cache.ApplyTelemetry("SIM",new AgvRobotTelemetry(0,0,0,1,"HOME",null,90,false,false,"Idle",null,null,null,[],[],DateTimeOffset.Now),TimeSpan.FromMilliseconds(1));
        await using var connections=new AgvConnectionManager(options); var gateway=new SeerAgvGateway(cache,connections,options);
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"test.db")});
        var catalog=new TrafficResourceCatalog([new("NARROW",TrafficResourceType.NarrowAisle)]); await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        var tasks=new DispatchTaskRepository(db); var outbox=new AgvCommandOutboxRepository(db); var orchestration=new DispatchOrchestrationRepository(db);
        var created=await tasks.TryCreateAsync("CNC-SIM","DUMPER-SIM",20,default); var id=created.Task.TaskId; await tasks.MarkAcknowledgedAsync(id,default);
        var step=(await tasks.GetStepsAsync(id,default))[0];
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(10)));
        await traffic.RecordRouteAsync(new(step.StepId,id,"SIM","HOME","PICK",["EDGE"],["NARROW"],false,"cancel protocol test"),default);
        await traffic.TryAcquireAsync(step.StepId,id,"SIM",["NARROW"],default);
        await outbox.TryPrepareTransportStepAsync(id,step.StepId,"SIM",["HOME","PICK"],new("JackLoad"),default);
        var command=(await outbox.GetDueCommandsAsync(DateTimeOffset.Now.AddSeconds(1),10,default)).Single();
        var claimed=(await outbox.TryClaimAsync(command.CommandId,DateTimeOffset.Now,default))!;
        var vendor=await gateway.CreateTransportTaskAsync(step.StepId,"SIM",["HOME","PICK"],new("JackLoad"),default);
        await outbox.MarkSentAsync(claimed.CommandId,vendor,default);
        var gate=new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running); var cancel=new TaskCancellationService(db,gate,gateway);
        var request=new TaskCancelRequest(Guid.NewGuid().ToString("N"),id,DispatchTaskStage.GoingToPickup,"模拟联调申请停止原运输并核对箱体PLC和路权")
            { RequirePickupNavigation=true };
        Check(Row().CancelRequiresPickupNavigation==false,"取消申请前尚未写入换单策略");
        var second=(await tasks.GetStepsAsync(id,default)).Skip(1).First();
        await db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=(int)DispatchTaskStepStatus.Ready})
            .Where(s=>s.StepId==second.StepId).ExecuteCommandAsync();
        var ineligible=await cancel.RequestAsync(TrafficUserContext.LocalOperation("isolated-test"),
            request with { RequestId=Guid.NewGuid().ToString("N") },default);
        Check(!ineligible.Accepted&&Row().CancelRequestId==null,"后续步骤已激活时拒绝按空车导航换单 "+mode);
        await db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=(int)DispatchTaskStepStatus.Pending})
            .Where(s=>s.StepId==second.StepId).ExecuteCommandAsync();
        Check((await cancel.RequestAsync(TrafficUserContext.LocalOperation("isolated-test"),request,default)).Accepted,"取消请求持久化 "+mode);
        Check(Row().CancelRequiresPickupNavigation,"换单取消策略持久化 "+mode);
        Check(Row().Stage==(int)DispatchTaskStage.CancellationPending && (await tasks.GetStepsAsync(id,default)).All(s=>s.Status==DispatchTaskStepStatus.ManualIntervention),"九步冻结并进入取消待确认 "+mode);
        Check((await orchestration.GetReadyStepsAsync(20,default)).Count==0 && (await orchestration.GetExecutingTasksAsync(default)).Count==0,"取消冻结后不进入正常调度推进 "+mode);
        Check((await outbox.GetDueCommandsAsync(DateTimeOffset.Now.AddMinutes(1),20,default)).Count==0,"取消后无运输命令可继续发送 "+mode);
        var auditCount=db.Queryable<ExecutionEvidenceEntity>().Count(); await cancel.RequestAsync(TrafficUserContext.LocalOperation("isolated-test"),request,default);
        Check(db.Queryable<ExecutionEvidenceEntity>().Count()==auditCount,"同请求重放不重复冻结或审计 "+mode);

        if(mode==CancelMode.MissingUnknown)
        {
            robot.State=404; await cancel.ProcessAsync(default);
            Check(robot.CancelCount==0 && Row().CancelConfirmedAt==null,"1110缺失任务不能认作取消成功");
            robot.State=999; await cancel.ProcessAsync(default);
            Check(robot.CancelCount==0 && Row().Stage==120,"未知状态不发送取消且不放行"); robot.State=2;
        }
        if(mode is CancelMode.AlreadyCompleted or CancelMode.AlreadyFailed) robot.State=mode==CancelMode.AlreadyCompleted?4:5;
        robot.PauseNextStatus();
        var pausedProcess=cancel.ProcessAsync(default);
        await robot.StatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using(var parallel=await gate.EnterAsync(MapExecutionOperation.ExistingWork,default).AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            Check(parallel is not null,"取消状态查询持有共享执行租约，其他车辆工作不被单车间查询串行 "+mode);
        robot.ReleaseStatus();
        await pausedProcess;
        if(mode==CancelMode.ResponseLost)
        {
            Check(Row().Stage==120 && Row().CancelSentAt==null && Row().CancelConfirmedAt==null,"3003已接收但响应断连时保留未确认意图");
            Check(db.Queryable<ExecutionEvidenceEntity>().Any(e=>e.Kind=="TaskCancellationProcessingFailed"),"取消响应丢失有持久化失败证据");
            await new TaskCancellationService(db,gate,gateway).ProcessAsync(default);
            Check(robot.CancelCount==1 && Row().Stage==130,"重建服务后1110确认结束不重复取消");
        }
        else if(mode is CancelMode.ResponseLostStillRunning or CancelMode.Rejected)
        {
            Check(Row().Stage==120 && Row().CancelSentAt==null,"取消拒绝或结果不明时保持待确认 "+mode);
            await new TaskCancellationService(db,gate,gateway).ProcessAsync(default);
            Check(robot.CancelCount==1,"持久化退避期间不立即重发3003 "+mode);
            await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.CancelNextAttemptAt==DateTime.Now.AddSeconds(-1)).Where(t=>t.TaskId==id).ExecuteCommandAsync();
            await cancel.ProcessAsync(default);
            Check(robot.CancelCount==2 && Row().CancelSentAt!=null && Row().Stage==120,"退避后重新查询原任务并向原车重试 "+mode);
            robot.State=6; await cancel.ProcessAsync(default);
        }
        else if(mode is not (CancelMode.AlreadyCompleted or CancelMode.AlreadyFailed))
        {
            Check(Row().Stage==120 && Row().CancelSentAt!=null && Row().CancelConfirmedAt==null,"3003接受不等同于1110结束确认 "+mode);
            await cancel.ProcessAsync(default); await cancel.ProcessAsync(default);
            Check(robot.CancelCount==1 && Row().Stage==120,"确认延迟期间只查询不重复取消 "+mode);
            robot.State=6; await cancel.ProcessAsync(default);
        }
        else Check(robot.CancelCount==0,"原任务已终结时不额外发送3003 "+mode);

        Check(Row().Stage==130 && Row().CancelConfirmedAt!=null,"原任务结束仅进入现场核对 "+mode);
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==id) && (await traffic.GetUnavailableResourceIdsAsync("OTHER",default)).Contains("NARROW"),"原运输结束仍保留路权阻止其他车辆 "+mode);
        Check((await tasks.GetStepsAsync(id,default)).All(s=>s.Status==DispatchTaskStepStatus.ManualIntervention),"车辆终结回执不完成九步或激活下一步 "+mode);
        Check(db.Queryable<ExecutionEvidenceEntity>().Count(e=>e.Kind=="TaskCancellationVehicleConfirmed")==1,"车辆结束确认保存一次审计 "+mode);
        await cancel.ProcessAsync(default);
        Check(db.Queryable<ExecutionEvidenceEntity>().Count(e=>e.Kind=="TaskCancellationVehicleConfirmed")==1,"核对阶段重复轮询不重复确认审计 "+mode);
        Check(robot.Targets.All(target=>target==step.StepId),"全部3003明确携带原运输编号 "+mode);
        Check(robot.CreateCount==1,"取消及重试未创建新运输 "+mode);
        File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{Mode=mode.ToString(),TaskId=id,StepId=step.StepId,robot.CreateCount,robot.CancelCount,Targets=robot.Targets,FinalStage=Row().Stage,LeaseRetained=true},new JsonSerializerOptions{WriteIndented=true}));
        DispatchTaskEntity Row()=>db.Queryable<DispatchTaskEntity>().Single(t=>t.TaskId==id);
    }
}
enum CancelMode { DelayedConfirmation,ResponseLost,ResponseLostStillRunning,Rejected,MissingUnknown,AlreadyCompleted,AlreadyFailed }
sealed class CancelRobot : IAsyncDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0); readonly CancellationTokenSource stop=new(); readonly Task server; readonly List<Task> clients=[]; readonly object sync=new();
    readonly CancelMode mode; string? accepted; int state=2;
    public int State { get=>Volatile.Read(ref state); set=>Volatile.Write(ref state,value); }
    public int CreateCount { get; private set; } public int CancelCount { get; private set; } public List<string> Targets { get; }=[];
    public TaskCompletionSource StatusStarted { get; private set; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StatusRelease { get; private set; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    int pauseNextStatus;
    public AgvRobotOptions Configuration { get; }
    public CancelRobot(CancelMode mode) {this.mode=mode; listener.Start(); Configuration=new(){VehicleId="SIM",IpAddress="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port}; Configuration.NavigationPort=Configuration.Port;server=Task.Run(Accept);}
    async Task Accept()
    {
        try {while(!stop.IsCancellationRequested) {var c=await listener.AcceptTcpClientAsync(stop.Token);lock(sync) clients.Add(Handle(c));}}
        catch(OperationCanceledException){}
    }
    async Task Handle(TcpClient client)
    {
        using(client) using(var stream=client.GetStream())
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16]; await stream.ReadExactlyAsync(header,stop.Token);var len=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4));if(len<0||len>1048576)throw new Exception("Bad frame");
                var body=new byte[len];await stream.ReadExactlyAsync(body,stop.Token);using var request=JsonDocument.Parse(body);var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2));object response;bool drop=false;
                if(type==1110&&Interlocked.Exchange(ref pauseNextStatus,0)==1)
                {
                    StatusStarted.TrySetResult();
                    await StatusRelease.Task.WaitAsync(stop.Token);
                }
                lock(sync)
                {
                    if(type==3066) {var moves=request.RootElement.GetProperty("move_task_list");accepted=moves[moves.GetArrayLength()-1].GetProperty("task_id").GetString();CreateCount++;response=new{ret_code=0};}
                    else if(type==1110) {var id=request.RootElement.GetProperty("task_ids")[0].GetString();response=new{task_status_list=new[]{new{task_id=id,status=id==accepted?State:404}}};}
                    else if(type==3003)
                    {
                        var target=request.RootElement.GetProperty("taskId").GetString()!;if(target!=accepted)throw new Exception("Cancel wrong task");Targets.Add(target);CancelCount++;
                        var first=CancelCount==1;drop=first&&mode is CancelMode.ResponseLost or CancelMode.ResponseLostStillRunning;
                        if(first&&mode==CancelMode.ResponseLost) State=6;
                        response=first&&mode==CancelMode.Rejected ? new{ret_code=1}:new{ret_code=0};
                    }
                    else throw new Exception("Unexpected request "+type);
                }
                if(drop)return;
                var bytes=JsonSerializer.SerializeToUtf8Bytes(response);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),bytes.Length);BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                await stream.WriteAsync(header,stop.Token);await stream.WriteAsync(bytes,stop.Token);await stream.FlushAsync(stop.Token);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or EndOfStreamException or IOException or SocketException){}
    }
    public void PauseNextStatus()
    {
        StatusStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        StatusRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref pauseNextStatus,1);
    }
    public void ReleaseStatus()=>StatusRelease.TrySetResult();
    public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();await server;Task[] pending;lock(sync)pending=clients.ToArray();try{await Task.WhenAll(pending);}finally{stop.Dispose();}}
}
