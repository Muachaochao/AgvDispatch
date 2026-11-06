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
        await TransportTimeoutRecoveryAsync();
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
        robot.Status=7;
        var timedOut=await gateway.GetTransportTaskStatusAsync("ORIGINAL",default);
        Check(timedOut is {State:AgvTransportTaskState.Failed,VendorStatusCode:7},"Robokit status 7 remains distinguishable from other failures");
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow,true,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Fatal,"blocked")),TimeSpan.Zero);
        Check((await gateway.GetVehiclesAsync(default)).Single().OperatingState=="Faulted","Fatal takes priority over the local blocked label");
    }
    static async Task CompletionGateAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"agv-obstacle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(directory,"test.db")});
        var vehicleIds=Enumerable.Range(0,12).Select(index=>"V"+index).ToArray();
        var catalog=new TrafficResourceCatalog(vehicleIds.Select(id=>new TrafficResourceDefinition(id+"-LANE",TrafficResourceType.NarrowAisle)).ToArray());
        await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        var tasks=new DispatchTaskRepository(db);var orchestration=new DispatchOrchestrationRepository(db);var outbox=new AgvCommandOutboxRepository(db);
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromMinutes(1)));var map=new ActiveMapRuntime();
        var cache=new AgvStateCache(Options(vehicleIds));var gateway=new TerminalGateway();
        var timeoutRecovery=new TransportTimeoutRecoveryService(db,cache,map,new AgvTransportChainLookup(db),traffic,null,new());
        var orchestrator=new DispatchTaskOrchestrator(NullLogger<DispatchTaskOrchestrator>.Instance,new(),orchestration,outbox,gateway,cache,map,new(),null!,traffic,null!,
            timeoutRecovery:timeoutRecovery);
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
        var a=await Executing(vehicleIds[0]);var b=await Executing(vehicleIds[1]);
        cache.ApplyTelemetry(vehicleIds[0],Telemetry(DateTimeOffset.UtcNow,true),TimeSpan.Zero);cache.ApplyTelemetry(vehicleIds[1],Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);
        await Sync();
        Check(!await Complete(a)&&await Complete(b),"one blocked vehicle does not finish on stale terminal status while another vehicle completes");
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId==a.Step),"blocked original transport retains its traffic resource");
        cache.ApplyTelemetry(vehicleIds[0],Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);await Sync();
        Check(await Complete(a),"fresh obstacle clear allows original completed transport to advance automatically");
        var sampleIndex=2;
        foreach(var sample in new[]{Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Error,"blocked")),Telemetry(DateTimeOffset.UtcNow,false,true),Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("52200",AgvAlarmSeverity.Fatal,"blocked")),Telemetry(DateTimeOffset.UtcNow,null),Telemetry(DateTimeOffset.UtcNow,false,false,new AgvAlarmSnapshot("51001",AgvAlarmSeverity.Error,"fault"))})
        {
            var vehicle=vehicleIds[sampleIndex++];var current=await Executing(vehicle);cache.ApplyTelemetry(vehicle,sample with{ReadAt=DateTimeOffset.UtcNow},TimeSpan.Zero);await Sync();
            Check(!await Complete(current),"52200/urgent/fatal/unknown/error state prevents Completed from advancing the original transport");
            cache.ApplyTelemetry(vehicle,Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);await Sync();Check(await Complete(current),"original identity resumes after fresh valid safe observation");
        }
        var timeoutVehicle=vehicleIds[sampleIndex++];
        var timeoutTask=await Executing(timeoutVehicle);
        gateway.TaskStates[timeoutTask.Step]=AgvTransportTaskState.Failed;
        gateway.TaskCodes[timeoutTask.Step]=7;
        cache.ApplyTelemetry(timeoutVehicle,Telemetry(DateTimeOffset.UtcNow),TimeSpan.Zero);
        await Sync();
        var timeoutStep=(await tasks.GetStepsAsync(timeoutTask.Task,default)).Single(s=>s.StepId==timeoutTask.Step);
        var timeoutSaved=db.Queryable<DispatchTaskEntity>().Single(t=>t.TaskId==timeoutTask.Task);
        Check(timeoutStep.Status==DispatchTaskStepStatus.ManualIntervention&&timeoutSaved.Stage==(int)DispatchTaskStage.Faulted,
            "orchestrator routes status 7 into an explicit timeout hold");
        Check(timeoutSaved.ErrorMessage!.Contains("取箱导航超时")&&timeoutSaved.ErrorMessage.Contains("运行地图不可用"),
            "timeout hold preserves stage context and reports unavailable recovery evidence");
        var staleVehicle=vehicleIds[sampleIndex];
        var stale=await Executing(staleVehicle);cache.MarkFailure(staleVehicle,new IOException("offline"),TimeSpan.FromSeconds(5));await Sync();
        Check(!await Complete(stale),"bad quality cannot promote a vendor terminal row to business completion");
        Check(gateway.Writes==0,"all obstacle recovery cycles issue zero cancel, replacement or navigation commands");
        Console.WriteLine("Evidence: "+directory);
    }
    static async Task TransportTimeoutRecoveryAsync()
    {
        var directory=Path.Combine(Path.GetTempPath(),"agv-timeout-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(directory,"test.db")});
        var mapRuntime=new ActiveMapRuntime();
        var definition=new MapDefinition("TIMEOUT","test","RobotWorld","m",
            [
                new("S",0,0,MapNodeType.Station,true,[],"S"),
                new("H",1,0,MapNodeType.Waypoint,true,[],"H"),
                new("D",1,1,MapNodeType.Waypoint,true,[],"D"),
                new("X",2,0,MapNodeType.Station,true,[],"X")
            ],
            [
                new("E-SH","S","H",1,1,false,true,[]),
                new("E-HX","H","X",1,1,false,true,[]),
                new("E-HD","H","D",1,1,false,true,[]),
                new("E-DX","D","X",1,1,false,true,[])
            ],[]);
        var catalog=new TrafficResourceCatalog(MapCorridorResources.AllResources(definition));
        await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        mapRuntime.Replace(new("MAP-T","MAPV-T",definition,DateTimeOffset.UtcNow));
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromMinutes(1)));
        var cache=new AgvStateCache(Options("A"));
        var chains=new AgvTransportChainLookup(db);
        var recovery=new TransportTimeoutRecoveryService(db,cache,mapRuntime,chains,traffic,null,new());
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var stationSequence=0;
        async Task<(string Task,string Step,string Station)> Seed(DispatchTaskStepType type)
        {
            var taskId="TASK-"+Guid.NewGuid().ToString("N");var stepId="STEP-"+Guid.NewGuid().ToString("N");
            var now=DateTime.Now;var station="CNC-"+(++stationSequence);
            await db.Insertable(new DispatchTaskEntity{TaskId=taskId,StationId=station,VehicleId="A",VendorTaskId=stepId,
                Stage=(int)DispatchTaskStage.GoingToPickup,Priority=0,CreatedAt=now,UpdatedAt=now}).ExecuteCommandAsync();
            await db.Insertable(new DispatchTaskStepEntity{StepId=stepId,TaskId=taskId,Sequence=1,StepType=(int)type,
                Status=(int)DispatchTaskStepStatus.Executing,SourceSite="S",DestinationSite="X",VehicleId="A",
                VendorTaskId=stepId,CreatedAt=now,UpdatedAt=now,StartedAt=now}).ExecuteCommandAsync();
            var payload=new AgvTransportCommandPayload(stepId,"A","S","X",["S","H","X"],null);
            await db.Insertable(new AgvCommandOutboxEntity{CommandId="CMD-"+Guid.NewGuid().ToString("N"),
                IdempotencyKey="TEST:"+stepId,TaskId=taskId,StepId=stepId,VehicleId="A",
                CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,
                PayloadJson=JsonSerializer.Serialize(payload,json),NextAttemptAt=now,CreatedAt=now,UpdatedAt=now,
                SentAt=now,VendorCommandId=stepId}).ExecuteCommandAsync();
            return(taskId,stepId,station);
        }
        var observed=DateTimeOffset.UtcNow;
        cache.ApplyTelemetry("A",Telemetry(observed) with{X=1,Y=0,CurrentStation="H"},TimeSpan.Zero);
        foreach(var type in new[]{DispatchTaskStepType.GoToPickup,DispatchTaskStepType.DeliverFullBoxToDumper,DispatchTaskStepType.ReturnEmptyBox})
        {
            var item=await Seed(type);var status=new AgvTransportTaskStatus(item.Step,item.Step,AgvTransportTaskState.Failed,
                "A",DateTimeOffset.UtcNow,"Robokit状态=7：超时"){VendorStatusCode=7};
            var task=new DispatchTaskWorkItem(item.Task,item.Station,"A",item.Step,DispatchTaskStage.GoingToPickup,0,
                DateTimeOffset.Now,DateTimeOffset.Now);
            var assessment=await recovery.CaptureAsync(task,status,default);
            Check(assessment is {Outcome:TransportTimeoutRecoveryOutcome.ReadyForControlledDetour},"status 7 creates a safe read-only detour candidate for "+type);
            Check(assessment!.CandidatePath.SequenceEqual(new[]{"H","D","X"}),"timeout detour starts at actual station and avoids inferred blocked edge for "+type);
            var step=db.Queryable<DispatchTaskStepEntity>().Single(s=>s.StepId==item.Step);
            var saved=db.Queryable<DispatchTaskEntity>().Single(t=>t.TaskId==item.Task);
            var snapshot=await recovery.GetAsync(item.Task,default);
            Check(step.Status==(int)DispatchTaskStepStatus.ManualIntervention,"timeout leaves executing state for "+type);
            Check(saved.Stage==(int)DispatchTaskStage.Faulted&&saved.ErrorMessage!.Contains("超时"),"timeout freezes original business task for "+type);
            Check(snapshot is {State:"ReadyForControlledDetour",CandidatePath.Count:3},"timeout recovery snapshot exposes the confirmable candidate for "+type);
            Check(db.Queryable<ExecutionEvidenceEntity>().Any(e=>e.TaskId==item.Task&&e.StepId==item.Step&&e.Kind=="TransportTimeoutRecoveryCaptured"),"timeout evidence is persisted for "+type);
        }
        var blocked=await Seed(DispatchTaskStepType.GoToPickup);
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow,true) with{X=1,Y=0,CurrentStation="H"},TimeSpan.Zero);
        var blockedStatus=new AgvTransportTaskStatus(blocked.Step,blocked.Step,AgvTransportTaskState.Failed,"A",
            DateTimeOffset.UtcNow,"Robokit状态=7：超时"){VendorStatusCode=7};
        var blockedTask=new DispatchTaskWorkItem(blocked.Task,blocked.Station,"A",blocked.Step,DispatchTaskStage.GoingToPickup,0,
            DateTimeOffset.Now,DateTimeOffset.Now);
        var blockedAssessment=await recovery.CaptureAsync(blockedTask,blockedStatus,default);
        Check(blockedAssessment is {Outcome:TransportTimeoutRecoveryOutcome.AwaitingObstacleClear},"active obstacle remains a hold instead of sending a detour");
        Check(!db.Queryable<AgvCommandOutboxEntity>().Any(c=>c.Status==(int)AgvCommandStatus.Pending),"timeout recovery sends no replacement command");
        var executable=await Seed(DispatchTaskStepType.GoToPickup);
        cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow) with
        {
            X=1,Y=0,CurrentStation="H",VelocityX=0,VelocityY=0,AngularVelocity=0
        },TimeSpan.Zero);
        var executableStatus=new AgvTransportTaskStatus(executable.Step,executable.Step,AgvTransportTaskState.Failed,
            "A",DateTimeOffset.UtcNow,"Robokit状态=7：超时"){VendorStatusCode=7};
        var executableTask=new DispatchTaskWorkItem(executable.Task,executable.Station,"A",executable.Step,
            DispatchTaskStage.GoingToPickup,0,DateTimeOffset.Now,DateTimeOffset.Now);
        var executableAssessment=await recovery.CaptureAsync(executableTask,executableStatus,default);
        var executableSnapshot=await recovery.GetAsync(executable.Task,default);
        Check(executableAssessment is {Outcome:TransportTimeoutRecoveryOutcome.ReadyForControlledDetour}&&executableSnapshot is not null,
            "captured timeout can be read as an executable confirmation candidate");
        var executionGate=new MapExecutionGate();executionGate.SetMode(MapExecutionMode.Running);
        var sentGateway=new RecoveryGateway(executable.Step);
        var plc=new FixedPlcSignals(executable.Station,"DUMPER",DateTimeOffset.UtcNow);
        var executableRecovery=new TransportTimeoutRecoveryService(db,cache,mapRuntime,
            new AgvTransportChainLookup(db),traffic,null,new(),null,
            sentGateway,executionGate,handshakeOptions:new HandshakeExecutionOptions{Enabled=false},
            signals:plc,creation:new DispatchTaskCreationOptions{DumperStationId="DUMPER"},
            dispatch:new DispatchOrchestratorOptions());
        var request=new TransportTimeoutRecoveryRequest(executable.Task,executableSnapshot!.Revision,true,
            true,true,true,"现场确认障碍已清理，车辆已停稳，箱体和PLC状态一致");
        var refresh=Task.Run(async()=>
        {
            await Task.Delay(250);
            cache.ApplyTelemetry("A",Telemetry(DateTimeOffset.UtcNow) with
            {
                X=1,Y=0,CurrentStation="H",VelocityX=0,VelocityY=0,AngularVelocity=0
            },TimeSpan.Zero);
        });
        var execution=await executableRecovery.ExecuteAsync(TrafficUserContext.LocalOperation("test"),request,default);
        await refresh;
        Check(execution.Accepted&&sentGateway.Writes==1,"confirmed timeout recovery sends exactly one replacement navigation");
        var reboundTask=db.Queryable<DispatchTaskEntity>().Single(t=>t.TaskId==executable.Task);
        var reboundStep=db.Queryable<DispatchTaskStepEntity>().Single(s=>s.StepId==executable.Step);
        Check(reboundTask.VendorTaskId==sentGateway.NewRoot&&reboundStep.Status==(int)DispatchTaskStepStatus.Executing,
            "replacement navigation binds the original business task to the new vendor root");
        var duplicate=await executableRecovery.ExecuteAsync(TrafficUserContext.LocalOperation("test"),
            request with { ExpectedRevision=execution.Snapshot!.Revision },default);
        Check(!duplicate.Accepted&&sentGateway.Writes==1,"repeating confirmation never resends the replacement navigation");
        Console.WriteLine("Timeout evidence: "+directory);
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
    public Dictionary<string,AgvTransportTaskState> TaskStates{get;}=[];
    public Dictionary<string,int?> TaskCodes{get;}=[];
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>
        Task.FromResult<AgvTransportTaskStatus?>(new(id,id,TaskStates.GetValueOrDefault(id,AgvTransportTaskState.Completed),
            Vehicles[id],DateTimeOffset.UtcNow){VendorStatusCode=TaskCodes.GetValueOrDefault(id)});
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct){Writes++;throw new Exception("unexpected motion");}
    public Task CancelTaskAsync(string id,CancellationToken ct){Writes++;throw new Exception("unexpected cancellation");}
}
sealed class RecoveryGateway(string originalId):IAgvGateway
{
    public string? NewRoot{get;private set;}public int Writes{get;private set;}
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>
        Task.FromResult<AgvTransportTaskStatus?>(new(id,id,
            id==originalId?AgvTransportTaskState.Failed:AgvTransportTaskState.GoingToDestination,
            "A",DateTimeOffset.UtcNow){VendorStatusCode=id==originalId?7:null});
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,string vehicle,
        IReadOnlyList<string> path,CancellationToken ct)=>GetTransportTaskStatusAsync(id,ct);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,
        AgvTransportAction? action,CancellationToken ct)
    {
        Writes++;NewRoot=id;
        return Task.FromResult(id);
    }
    public Task CancelTaskAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
}
sealed class FixedPlcSignals:IPlcSignalCache
{
    readonly Dictionary<string,PlcSignalSnapshot> snapshots;
    public FixedPlcSignals(string station,string dumper,DateTimeOffset now)
    {
        var values=new Dictionary<(string Station,string Variable),bool>
        {
            [(station,"Faulted")]=false,[(station,"CommunicationOnline")]=true,
            [(station,"FullBoxRequested")]=false,[(station,"FullBoxRequestAcknowledged")]=true,
            [(station,"BoxPresent")]=true,[(station,"FullBoxRemoved")]=false,
            [(station,"EmptyBoxTaskAcknowledged")]=false,[(station,"EmptyBoxPlaced")]=false,
            [(dumper,"Faulted")]=false
        };
        snapshots=values.ToDictionary(pair=>pair.Key.Station+"\u001F"+pair.Key.Variable,
            pair=>new PlcSignalSnapshot(pair.Key.Station,pair.Key.Variable,pair.Key.Variable,
                "DB1."+pair.Key.Variable,"Bool","Read","Read",true,pair.Value,
                SignalQuality.Good,now,null,1),StringComparer.OrdinalIgnoreCase);
    }
    public event Action<PlcSignalChanged>? SignalChanged;
    public long Version=>1;
    public IReadOnlyList<PlcSignalSnapshot> GetAll()=>snapshots.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string stationId)=>snapshots.Values
        .Where(s=>s.StationId.Equals(stationId,StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool TryGet(string stationId,string variableName,out PlcSignalSnapshot snapshot)=>
        snapshots.TryGetValue(stationId+"\u001F"+variableName,out snapshot!);
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
