using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
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
    static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    static async Task Main()
    {
        TimingAndPolicy();
        await CommandGateAsync();
        await CompletionGateAsync();
        Console.WriteLine($"PASS: {checks} obstacle safety checks; isolated database and loopback protocol only; no physical AGV or PLC.");
    }
    static AgvOptions Options(params string[] ids)=>new(){Enabled=true,AllowCommands=true,Robots=ids.Select(id=>new AgvRobotOptions{VehicleId=id,IpAddress="127.0.0.1"}).ToList()};
    static AgvRobotTelemetry Telemetry(DateTimeOffset time,bool? blocked=false,bool? emergency=false,params AgvAlarmSnapshot[] alarms)=>
        new(0,0,0,.99,"HOME",null,90,blocked,emergency,"Running",null,null,null,[],alarms,time);
    static AgvRobotSnapshot Read(AgvStateCache cache,string id="A"){if(!cache.TryGet(id,out var snapshot))throw new Exception("missing vehicle");return snapshot;}
    static void TimingAndPolicy()
    {
        var clock=new ManualClock(DateTimeOffset.UtcNow);var cache=new AgvStateCache(Options("A"),clock);
        foreach(var code in new string?[]{"52200"," 52200 "})Check(AgvObstaclePolicy.IsObstacleCode(code),"exact normalized obstacle code "+code);
        foreach(var code in new string?[]{null,"152200","522001","UNKNOWN"})Check(!AgvObstaclePolicy.IsObstacleCode(code),"other code remains a fault "+code);
        cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true),TimeSpan.Zero);var start=clock.GetUtcNow();
        Check(Read(cache).ObstacleSince==start,"blocked telemetry starts an observation interval");
        for(var i=0;i<35;i++){clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true),TimeSpan.Zero);}
        Check(Read(cache).ObstacleSince==start&&Read(cache).LastSuccessfulPollAt-start==TimeSpan.FromSeconds(35),"35 seconds of fresh observations preserve continuous blockage");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow()),TimeSpan.Zero);
        Check(Read(cache).ObstacleSince is null&&Read(cache).LastObstacleClearedAt==clock.GetUtcNow(),"fresh explicit clear ends interval and records recovery observation");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),false,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Error,"blocked")),TimeSpan.Zero);
        Check(AgvObstaclePolicy.IsBlocked(Read(cache))&&Read(cache).ObstacleSince==clock.GetUtcNow(),"52200 alone still starts a local wait when blocked=false");
        Check(!AgvObstaclePolicy.HasBlockingAlarm(Read(cache)),"local Error 52200 does not freeze the fleet");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Fatal,"blocked")),TimeSpan.Zero);
        Check(AgvObstaclePolicy.HasBlockingAlarm(Read(cache)),"Fatal 52200 is never exempted as a local obstacle");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Error,"blocked"),new AgvAlarmSnapshot("51001",AgvAlarmSeverity.Error,"motor fault")),TimeSpan.Zero);
        Check(AgvObstaclePolicy.HasBlockingAlarm(Read(cache)),"a concurrent unrelated error retains priority");
        var duplicate=clock.GetUtcNow();cache.ApplyTelemetry("A",Telemetry(duplicate),TimeSpan.Zero);
        Check(Read(cache).Blocked==true&&Read(cache).ObstacleSince is null&&Read(cache).LastSuccessfulPollAt==duplicate,"duplicate cannot replace safety state or count as continuous recovery");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true),TimeSpan.Zero);var restart=clock.GetUtcNow();
        cache.ApplyTelemetry("A",Telemetry(restart.AddSeconds(-2)),TimeSpan.Zero);
        Check(Read(cache).ObstacleSince is null&&Read(cache).Blocked==true,"out-of-order clear cannot overwrite newer blocked observation");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true),TimeSpan.Zero);
        cache.MarkFailure("A",new IOException("isolated test"),TimeSpan.FromSeconds(5));
        Check(Read(cache).ObstacleSince is null,"communication failure breaks continuous obstacle timing");
        clock.Advance(TimeSpan.FromSeconds(1));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true),TimeSpan.Zero);
        Check(Read(cache).ObstacleSince==clock.GetUtcNow(),"reconnection starts a new evidence interval");
        clock.Advance(TimeSpan.FromSeconds(6));cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow(),true),TimeSpan.Zero);
        Check(Read(cache).ObstacleSince==clock.GetUtcNow(),"unobserved stale gap is excluded from continuous timing");
        cache.SetCommunicationEnabled("A",false);Check(Read(cache).ObstacleSince is null,"operator disconnect clears continuous timer");
        cache.SetCommunicationEnabled("A",true);clock.Advance(TimeSpan.FromSeconds(1));
        cache.ApplyTelemetry("A",Telemetry(clock.GetUtcNow().AddSeconds(-10),true),TimeSpan.Zero);
        Check(Read(cache).Quality==SignalQuality.Stale&&Read(cache).ObstacleSince is null,"an expired sample never establishes a valid continuous wait");
    }
    static async Task CommandGateAsync()
    {
        await using var robot=new ReadOnlyRobot();var options=Options("A");options.RequestIntervalMs=100;
        options.Robots[0].Port=robot.Port;options.Robots[0].NavigationPort=robot.Port;
        var cache=new AgvStateCache(options);await using var manager=new AgvConnectionManager(options);
        var gateway=new SeerAgvGateway(cache,manager,options);
        foreach(var sample in new[]{Telemetry(DateTimeOffset.UtcNow,true),Telemetry(DateTimeOffset.UtcNow,null),Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Error,"blocked"))})
        {
            cache.ApplyTelemetry("A",sample with{ReadAt=DateTimeOffset.UtcNow},TimeSpan.Zero);
            var rejected=false;try{await gateway.CreateTransportTaskAsync("NEW","A",["HOME","TARGET"],null,default);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&robot.Writes==0,"blocked/unknown/52200 state refuses any new navigation command");
        }
        Check((await gateway.GetVehiclesAsync(default)).Single().OperatingState=="Blocked","local 52200 is displayed as blocked rather than fatal");
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow,true,null),TimeSpan.Zero);
        Check((await gateway.GetVehiclesAsync(default)).Single().OperatingState=="Unknown","unknown emergency state takes priority over local obstacle waiting");
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);
        cache.ApplyTelemetry("A",Telemetry(Read(cache).LastSuccessfulPollAt!.Value),TimeSpan.Zero);
        Check((await gateway.GetVehiclesAsync(default)).Single().OperatingState=="Unknown","stale duplicate telemetry is never advertised as an idle dispatch candidate");
        robot.Status=2;
        Check(await gateway.CreateTransportTaskAsync("ORIGINAL","A",["HOME","TARGET"],null,default)=="ORIGINAL"&&robot.Writes==0,"accepted original identity is read-only and never cancelled or re-sent during blockage");
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow,true,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Fatal,"blocked")),TimeSpan.Zero);
        Check((await gateway.GetVehiclesAsync(default)).Single().OperatingState=="Faulted","Fatal takes priority over the local blocked label");
    }
    static async Task CompletionGateAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"agv-obstacle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(directory,"test.db")});
        var catalog=new TrafficResourceCatalog([new("A-LANE",TrafficResourceType.NarrowAisle),new("B-LANE",TrafficResourceType.NarrowAisle)]);
        await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        var tasks=new DispatchTaskRepository(db);var orchestration=new DispatchOrchestrationRepository(db);var outbox=new AgvCommandOutboxRepository(db);
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromMinutes(1)));var map=new ActiveMapRuntime();
        var cache=new AgvStateCache(Options("A","B"));var gateway=new TerminalGateway();
        var orchestrator=new DispatchTaskOrchestrator(NullLogger<DispatchTaskOrchestrator>.Instance,new(),orchestration,outbox,gateway,cache,map,new(),null!,traffic,null!);
        async Task<(string Task,string Step)> Executing(string vehicle)
        {
            var created=await tasks.TryCreateAsync("CNC-"+Guid.NewGuid().ToString("N"),"DUMPER",10,default);await tasks.MarkAcknowledgedAsync(created.Task.TaskId,default);
            var step=(await tasks.GetStepsAsync(created.Task.TaskId,default))[0];
            Check(await outbox.TryPrepareTransportStepAsync(created.Task.TaskId,step.StepId,vehicle,["HOME","TARGET"],null,default),"prepare isolated original transport "+vehicle);
            var due=(await outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddSeconds(1),100,default)).Single(c=>c.StepId==step.StepId);
            await outbox.TryClaimAsync(due.CommandId,DateTimeOffset.UtcNow,default);await outbox.MarkSentAsync(due.CommandId,step.StepId,default);
            gateway.Vehicles[step.StepId]=vehicle;await traffic.TryAcquireAsync(step.StepId,created.Task.TaskId,vehicle,[vehicle+"-LANE"],default);
            return(created.Task.TaskId,step.StepId);
        }
        async Task Sync()=>await (Task)typeof(DispatchTaskOrchestrator).GetMethod("SynchronizeExecutingTasksAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(orchestrator,[CancellationToken.None])!;
        async Task<bool> Complete((string Task,string Step) item)=>(await tasks.GetStepsAsync(item.Task,default)).Single(s=>s.StepId==item.Step).Status==DispatchTaskStepStatus.Completed;
        var a=await Executing("A");var b=await Executing("B");
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow,true),TimeSpan.Zero);cache.ApplyTelemetry("B",Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);
        await Sync();
        Check(!await Complete(a)&&await Complete(b),"one blocked vehicle does not finish on stale terminal status while another vehicle completes");
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId==a.Step),"blocked original transport retains its traffic resource");
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);await Sync();
        Check(await Complete(a),"fresh obstacle clear allows original completed transport to advance automatically");
        foreach(var sample in new[]{Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Error,"blocked")),Telemetry(DateTimeOffset.UtcNow,false,true),Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Fatal,"blocked")),Telemetry(DateTimeOffset.UtcNow,null),Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("51001",AgvAlarmSeverity.Error,"fault"))})
        {
            var current=await Executing("A");cache.ApplyTelemetry("A",sample with{ReadAt=DateTimeOffset.UtcNow},TimeSpan.Zero);await Sync();
            Check(!await Complete(current),"52200/urgent/fatal/unknown/error state prevents Completed from advancing the original transport");
            cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);await Sync();Check(await Complete(current),"original identity resumes after fresh valid safe observation");
        }
        var stale=await Executing("A");cache.MarkFailure("A",new IOException("offline"),TimeSpan.FromSeconds(5));await Sync();
        Check(!await Complete(stale),"bad quality cannot promote a vendor terminal row to business completion");
        Check(gateway.Writes==0,"all obstacle recovery cycles issue zero cancel, replacement or navigation commands");
        Console.WriteLine("Evidence: "+directory);
    }
}

sealed class ManualClock(DateTimeOffset start):TimeProvider
{
    DateTimeOffset current=start;public override DateTimeOffset GetUtcNow()=>current;
    public void Advance(TimeSpan span)=>current+=span;
}
sealed class TerminalGateway:IAgvGateway
{
    public Dictionary<string,string> Vehicles{get;}=[];public int Writes{get;private set;}
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>Task.FromResult<AgvTransportTaskStatus?>(new(id,id,AgvTransportTaskState.Completed,Vehicles[id],DateTimeOffset.UtcNow));
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct){Writes++;throw new Exception("unexpected motion");}
    public Task CancelTaskAsync(string id,CancellationToken ct){Writes++;throw new Exception("unexpected cancellation");}
}
sealed class ReadOnlyRobot:IAsyncDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();readonly Task server;
    public int Port{get;}public int Status=404;public int Writes;
    public ReadOnlyRobot(){listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;server=Serve();}
    async Task Serve()
    {
        try
        {
            using var client=await listener.AcceptTcpClientAsync(stop.Token);using var stream=client.GetStream();
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16];await stream.ReadExactlyAsync(header,stop.Token);var body=new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4))];await stream.ReadExactlyAsync(body,stop.Token);
                var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2));if(type!=1110){Writes++;throw new Exception("unexpected non-read protocol");}
                using var request=JsonDocument.Parse(body);var ids=request.RootElement.GetProperty("task_ids").EnumerateArray().Select(x=>x.GetString()).ToArray();
                var result=JsonSerializer.SerializeToUtf8Bytes(new{task_status_list=ids.Select(id=>new{task_id=id,status=Status}).ToArray()});
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),result.Length);BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                await stream.WriteAsync(header,stop.Token);await stream.WriteAsync(result,stop.Token);
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or IOException or SocketException){}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();await server;stop.Dispose();}
}
