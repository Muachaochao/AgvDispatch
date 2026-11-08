using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Mvvm;

internal static class TestBackend
{
    public static async Task Run(string root,string pipeName)
    {
        if(!Directory.Exists(root) || !Path.GetFileName(root).StartsWith("agv-hosted-")) throw new Exception("Invalid private directory");
        using var logs=LoggerFactory.Create(b=>b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        using var db=DatabaseFactory.Create(new() { DatabasePath=Path.Combine(root,"test.db") });
        var resource=new TrafficResourceDefinition("SHARED",TrafficResourceType.NarrowAisle);
        var catalog=new TrafficResourceCatalog([resource]); var init=new DatabaseInitializer(db,catalog); await init.InitializeAsync(default);
        var tasks=new DispatchTaskRepository(db); var orchestration=new DispatchOrchestrationRepository(db); var outbox=new AgvCommandOutboxRepository(db);
        var map=new ActiveMapRuntime();
        var sites=new[]{"HOME1","HOME2","PICK1","PICK2","WAIT","DUMP"};
        var nodes=sites.Select((s,i)=>new MapNode(s,i*2,0,MapNodeType.Station,true,[],s)).ToArray();
        var edges=new[]{("HOME1","PICK1"), ("HOME2","PICK2"), ("PICK1","WAIT"), ("PICK2","WAIT"), ("WAIT","DUMP"), ("DUMP","PICK1"), ("DUMP","PICK2")}
            .Select((p,i)=>new MapEdge("E"+i,p.Item1,p.Item2,2,1,false,true,["SHARED"])).ToArray();
        var definition=new MapDefinition("integration","","LocalCartesian","m",nodes,edges,[resource]);
        map.Replace(new("MAP-INTEGRATION","V1",definition,DateTimeOffset.Now));
        var gate=new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
        // The hosted fixture uses the legacy simulated PLC handshake. Keep it serial
        // because multi-task route ownership is gated by the current 28-stage contract.
        var dispatch=new DispatchOrchestratorOptions { Enabled=true,MaxConcurrentTasks=1,DumperWaitingSite="WAIT",DumperDumpingSite="DUMP",
            StationPickupSites=new() { ["CNC001"]="PICK1",["CNC002"]="PICK2" } };
        foreach(var (step,action) in new[]{("GoToPickup","JackLoad"),("DeliverFullBoxToDumper","Wait"),("EnterDumpingPosition","Wait"),("ReturnEmptyBox","JackUnload")}) dispatch.StepActions[step]=new(){Operation=action};
        var creation=new DispatchTaskCreationOptions { AcknowledgementRetryIntervalMs=500 }; var parameters=new TaskParameterRuntime(dispatch,creation);
        var plc=new SimulatedPlc(); var stations=new SimulatedStations(plc);
        using var robot1=new SimulatedRobot("AGV-01","HOME1",nodes); using var robot2=new SimulatedRobot("AGV-02","HOME2",nodes);
        var options=new AgvOptions { Enabled=true,AllowCommands=true,PollIntervalMs=200,Robots=[robot1.Configuration,robot2.Configuration] };
        var states=new AgvStateCache(options); var manager=new AgvConnectionManager(options);
        var gateway=new SeerAgvGateway(states,manager,options); var flow=new EmptyFlow();
        var mapOptions=new MapOptions { Enabled=true };
        var planner=new ConfiguredRoutePlanner(new MapDefinitionProvider(new MapOptions(),new MapImportService(),new MapValidationService()),map,dispatch,flow);
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(120))); var control=new TrafficResourceControlService(db,catalog);
        var manual=new ManualTaskCreationService(tasks,stations,gateway,states,map,gate,orchestration,creation,dispatch,new(){PollIntervalMs=100},mapOptions,plc,parameters);
        var settings=new TaskParameterSettingsService(new(Path.Combine(root,"parameters.json")),parameters,manual,new DispatchTaskReadRepository(db),map,gate);
        var queries=new TaskCenterQueryService(new TaskCenterReadRepository(db),plc,creation,parameters);
        var ack=new TaskCreationCoordinator(logs.CreateLogger<TaskCreationCoordinator>(),creation,stations,tasks,plc,gate,parameters);
        var orchestrator=new DispatchTaskOrchestrator(logs.CreateLogger<DispatchTaskOrchestrator>(),dispatch,orchestration,outbox,gateway,states,map,mapOptions,planner,traffic,gate);
        var sender=new AgvCommandOutboxDispatcher(logs.CreateLogger<AgvCommandOutboxDispatcher>(),new(){PollIntervalMs=100},outbox,gateway,gate);
        var steps=new DispatchTaskStepCoordinator(logs.CreateLogger<DispatchTaskStepCoordinator>(),new(){PollIntervalMs=100},dispatch,creation,orchestration,outbox,plc,plc,planner,traffic,gate,vehicleStates:states,activeMap:map);
        // Unused endpoints fail closed; the exercised endpoints use their real services.
        var args=typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(p=>p.ParameterType switch
        {
            var t when t==typeof(Microsoft.Extensions.Logging.ILogger<TrafficControlNamedPipeServer>) => (object)logs.CreateLogger<TrafficControlNamedPipeServer>(),
            var t when t==typeof(TrafficSecurityOptions) => new TrafficSecurityOptions { PipeName=pipeName },
            var t when t==typeof(IDatabaseInitializer) => init,
            var t when t==typeof(ITrafficResourceControlService) => control,
            var t when t==typeof(ITrafficFlowMonitor) => flow,
            var t when t==typeof(IMapExecutionGate) => gate,
            var t when t==typeof(ITaskCenterQueryService) => queries,
            var t when t==typeof(IManualTaskCreationService) => manual,
            var t when t==typeof(ITaskParameterService) => settings,
            var t when t==typeof(NamedPipeDiagnostics) => new NamedPipeDiagnostics(),
            _ => null
        }).ToArray();
        using var server=(TrafficControlNamedPipeServer)Activator.CreateInstance(typeof(TrafficControlNamedPipeServer),args)!;

        var scanner=new DeviceScanner(db,plc,robot1,robot2,root);
        var poller=new AgvPollingService(logs.CreateLogger<AgvPollingService>(),options,manager,states);
        var builder=Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        foreach(var service in new IHostedService[]{poller,ack,orchestrator,sender,steps,server,scanner}) builder.Services.AddSingleton<IHostedService>(service);
        using var host=builder.Build();
        try
        {
            await host.StartAsync();
            File.WriteAllText(Path.Combine(root,"ready.json"),JsonSerializer.Serialize(new {ProcessId=Environment.ProcessId,Pipe=pipeName,Robots=options.Robots,SimulatedPlc=true}));
            while(!File.Exists(Path.Combine(root,"stop.request"))) await Task.Delay(100);
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
        finally { await manager.DisposeAsync(); }
    }
}
sealed class DeviceScanner(SqlSugar.ISqlSugarClient db,SimulatedPlc plc,SimulatedRobot first,SimulatedRobot second,string root) : IsolatedBackgroundService
{
    readonly Dictionary<string,DateTimeOffset> seen=[];
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            plc.Scan();
            var tasks=await db.Queryable<DispatchTaskEntity>().ToListAsync(ct);
            foreach(var task in tasks.Where(t=>t.Stage!=90 && t.Stage!=110))
            {
                var step=await db.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==task.TaskId && s.Status!=(int)DispatchTaskStepStatus.Completed).OrderBy(s=>s.Sequence).FirstAsync(ct);
                if(step==null)continue;
                if(!seen.TryGetValue(step.StepId,out var since)){since=DateTimeOffset.UtcNow;seen[step.StepId]=since;}
                // Hold the first transport long enough for the independent frontend to observe contention.
                if(DateTimeOffset.UtcNow-since<TimeSpan.FromMilliseconds(step.Sequence==1?2000:500))continue;
                if(step.Status==(int)DispatchTaskStepStatus.Executing)
                {
                    var robot=step.VehicleId=="AGV-01"?first:second;
                    if(robot.Commands.ContainsKey(step.StepId))robot.Complete(step.StepId);
                }
                if(step.Status!=(int)DispatchTaskStepStatus.Ready)continue;
                switch((DispatchTaskStepType)step.StepType)
                {
                    case DispatchTaskStepType.PickupFullBox:plc.Set(task.StationId,"BoxPresent",false);break;
                    case DispatchTaskStepType.WaitForDumperReady:plc.Set("DUMPER-001","DumperReady",true);break;
                    case DispatchTaskStepType.DumpFullBox:plc.Set("DUMPER-001","DumpingCompletedMayLeave",true);break;
                    case DispatchTaskStepType.PlaceEmptyBox:plc.Set(task.StationId,"BoxPresent",true);break;
                }
            }
            if(tasks.Count==2 && tasks.All(t=>t.Stage==90) && !File.Exists(Path.Combine(root,"devices.json")))
            {
                var evidence=JsonSerializer.Serialize(new {First=first.Commands,Second=second.Commands,PlcWrites=plc.Writes.Select(w=>new{w.Station,w.Name,w.Value}).ToArray(),Inputs=plc.GetAll(),LeaseCount=await db.Queryable<TrafficResourceLeaseEntity>().CountAsync(ct)},new JsonSerializerOptions{WriteIndented=true});
                File.WriteAllText(Path.Combine(root,"devices.tmp"),evidence); File.Move(Path.Combine(root,"devices.tmp"),Path.Combine(root,"devices.json"));
            }
            await Task.Delay(100,ct);
        }
    }
}
