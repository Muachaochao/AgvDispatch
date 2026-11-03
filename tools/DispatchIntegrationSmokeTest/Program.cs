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

internal static class Program
{
    static int checks;
    [STAThread] static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Run(); Console.WriteLine($"PASS: {checks} integration checks; loopback TCP + real Named Pipe + isolated SQLite + simulated PLC only."); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); checks++; Console.WriteLine("PASS "+message); }
    static void Pump(Func<bool> done)
    {
        if(done()) return; var frame=new DispatcherFrame(); var deadline=DateTime.UtcNow.AddSeconds(20);
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(10) };
        timer.Tick+=(_,_)=> { if(done() || DateTime.UtcNow>deadline) frame.Continue=false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); if(!done()) throw new TimeoutException("Integration operation timed out");
    }
    static void Await(Task task) { Pump(()=>task.IsCompleted); task.GetAwaiter().GetResult(); }
    static T Await<T>(Task<T> task) { Pump(()=>task.IsCompleted); return task.GetAwaiter().GetResult(); }
    // Drive real backend cycles deterministically, without shortening production polling thresholds.
    static void Cycle(object component,string name)
    {
        var method=component.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
        Await((Task)method.Invoke(component,method.GetParameters().Length==0?[]:[CancellationToken.None])!);
    }
    static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"agv-integration-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Console.WriteLine("Evidence: "+root);
        var timeline=new List<object>();
        using var db=DatabaseFactory.Create(new() { DatabasePath=Path.Combine(root,"test.db") });
        var resource=new TrafficResourceDefinition("SHARED",TrafficResourceType.NarrowAisle);
        var catalog=new TrafficResourceCatalog([resource]); var init=new DatabaseInitializer(db,catalog); Await(init.InitializeAsync(default));
        var tasks=new DispatchTaskRepository(db); var orchestration=new DispatchOrchestrationRepository(db);
        var map=new ActiveMapRuntime();
        var sites=new[]{"HOME1","HOME2","PICK1","PICK2","WAIT","DUMP"};
        var nodes=sites.Select((s,i)=>new MapNode(s,i*2,0,MapNodeType.Station,true,[],s)).ToArray();
        var edges=new[]{("HOME1","PICK1"), ("HOME2","PICK2"), ("PICK1","WAIT"), ("PICK2","WAIT"), ("WAIT","DUMP"), ("DUMP","PICK1"), ("DUMP","PICK2")}
            .Select((p,i)=>new MapEdge("E"+i,p.Item1,p.Item2,2,1,false,true,["SHARED"])).ToArray();
        var definition=new MapDefinition("integration","","LocalCartesian","m",nodes,edges,[resource]);
        map.Replace(new("MAP-INTEGRATION","V1",definition,DateTimeOffset.Now));
        var outbox=new AgvCommandOutboxRepository(db,map);
        var gate=new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
        var engineeringOnly=Environment.GetCommandLineArgs().Contains("--engineering-runtime-only");
        var dispatch=new DispatchOrchestratorOptions { Enabled=true,MaxConcurrentTasks=engineeringOnly?1:2,DumperWaitingSite="WAIT",DumperDumpingSite="DUMP",
            StationPickupSites=new() { ["CNC001"]="PICK1",["CNC002"]="PICK2" } };
        foreach(var (step,action) in new[]{("GoToPickup","JackLoad"),("DeliverFullBoxToDumper","Wait"),("EnterDumpingPosition","Wait"),("ReturnEmptyBox","JackUnload")}) dispatch.StepActions[step]=new(){Operation=action};
        var creation=new DispatchTaskCreationOptions(); var parameters=new TaskParameterRuntime(dispatch,creation);
        var plc=new SimulatedPlc(); var stations=new SimulatedStations(plc);
        using var robot1=new SimulatedRobot("AGV-01","HOME1",nodes); using var robot2=new SimulatedRobot("AGV-02","HOME2",nodes);
        var options=new AgvOptions { Enabled=true,AllowCommands=true,Robots=[robot1.Configuration,robot2.Configuration] };
        var states=new AgvStateCache(options); var manager=new AgvConnectionManager(options);
        var gateway=new SeerAgvGateway(states,manager,options); var flow=new EmptyFlow();
        var mapOptions=new MapOptions { Enabled=true };
        var planner=new ConfiguredRoutePlanner(new MapDefinitionProvider(new MapOptions(),new MapImportService(),new MapValidationService()),map,dispatch,flow);
        var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(120))); var control=new TrafficResourceControlService(db,catalog);
        var manual=new ManualTaskCreationService(tasks,stations,gateway,states,map,gate,orchestration,creation,dispatch,new(),mapOptions,plc,parameters);
        var settings=new TaskParameterSettingsService(new(Path.Combine(root,"parameters.json")),parameters,manual,new DispatchTaskReadRepository(db),map,gate);
        var queries=new TaskCenterQueryService(new TaskCenterReadRepository(db),plc,creation,parameters);
        var ack=new TaskCreationCoordinator(NullLogger<TaskCreationCoordinator>.Instance,creation,stations,tasks,plc,gate,parameters);
        var orchestrator=new DispatchTaskOrchestrator(NullLogger<DispatchTaskOrchestrator>.Instance,dispatch,orchestration,outbox,gateway,states,map,mapOptions,planner,traffic,gate);
        var sender=new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,new(),outbox,gateway,gate);
        var steps=new DispatchTaskStepCoordinator(NullLogger<DispatchTaskStepCoordinator>.Instance,new(),dispatch,creation,orchestration,outbox,plc,plc,planner,traffic,gate,vehicleStates:states,activeMap:map);
        var pipeName="AgvIntegration-"+Guid.NewGuid().ToString("N");
        // Unused endpoints fail closed; the exercised endpoints use their real services.
        var args=typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(p=>p.ParameterType switch
        {
            var t when t==typeof(Microsoft.Extensions.Logging.ILogger<TrafficControlNamedPipeServer>) => (object)NullLogger<TrafficControlNamedPipeServer>.Instance,
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
        using var client=new NamedPipeTrafficControlClient(pipeName); TaskCenterViewModel? vm=null;
        try
        {
            Await(server.StartAsync(default)); Poll();
            if(engineeringOnly)
            {
            var profile=parameters.Capture() with
            {
                EngineeringSource=new(Guid.NewGuid().ToString("N"),new string('A',64),EngineeringRuntimeIdentity.MapHash(definition)),
                Pickups=[new("CNC001","PICK1"){VehiclePoolId="POOL-A",AllowedVehicleIds=["AGV-01"]},new("CNC002","PICK2"){VehiclePoolId="POOL-B",AllowedVehicleIds=["AGV-02"]}]
            };
            var profileRequest=new TaskParameterRequest(Guid.NewGuid().ToString("N"),parameters.Revision,"MAP-INTEGRATION","V1",profile);
            var applied=Await(settings.SaveAsync(TrafficUserContext.LocalOperation("isolated-engineering"),profileRequest,default));
            Check(applied.Accepted&&applied.Snapshot?.SavedRequestId==profileRequest.RequestId,"工程参数通过真实九步校验并保存原申请回执："+applied.Message);
            Check(Await(settings.SaveAsync(TrafficUserContext.LocalOperation("isolated-engineering"),profileRequest,default)).Accepted&&applied.Snapshot!.Revision==parameters.Revision,"原编号重试不更改修订或重复应用");
            var restoredOptions=new DispatchOrchestratorOptions();var restored=new TaskParameterRuntime(restoredOptions,new());restored.Apply(TaskParameterSettingsService.ReadDocument(Path.Combine(root,"parameters.json"))!);
            Check(restoredOptions.AllowsVehicle("CNC001","AGV-01")&&!restoredOptions.AllowsVehicle("CNC001","AGV-02")&&restored.Capture().EngineeringSource==profile.EngineeringSource,"真实持久化重载保留工程来源和车辆池");
            var stripped=profile with {EngineeringSource=null,Pickups=profile.Pickups.Select(p=>new TaskPickupParameter(p.StationId,p.PickupSite)).ToArray()};
            Check(!Await(settings.SaveAsync(TrafficUserContext.LocalOperation("isolated-engineering"),new(Guid.NewGuid().ToString("N"),parameters.Revision,"MAP-INTEGRATION","V1",stripped),default)).Accepted,"旧客户端不能静默移除工程和池限制");
            var modifiedPool=profile with {Pickups=[profile.Pickups[0] with {AllowedVehicleIds=["AGV-02"]},profile.Pickups[1]]};
            Check(!Await(settings.SaveAsync(TrafficUserContext.LocalOperation("isolated-engineering"),new(Guid.NewGuid().ToString("N"),parameters.Revision,"MAP-INTEGRATION","V1",modifiedPool),default)).Accepted,"同一冻结工程版本不能在参数页改写车辆池");
            var wrongHash=profile with {EngineeringSource=profile.EngineeringSource! with {MapDefinitionHash=new string('0',64)}};
            Check(!Await(settings.SaveAsync(TrafficUserContext.LocalOperation("isolated-engineering"),new(Guid.NewGuid().ToString("N"),parameters.Revision,"MAP-INTEGRATION","V1",wrongHash),default)).Accepted,"地图内容摘要不同的工程参数不能保存");
            var wrongVehicle=Await(manual.CreateAsync(TrafficUserContext.LocalOperation("isolated-engineering"),new(Guid.NewGuid().ToString("N"),"CNC001","AGV-02",50,"MAP-INTEGRATION","V1"){ParameterRevision=parameters.Revision},default));
            Check(!wrongVehicle.Accepted&&wrongVehicle.Message.Contains("车辆池"),"手动指定池外车辆在持久化任务前被拦截");
            var first=Await(client.CreateTaskAsync(new(Guid.NewGuid().ToString("N"),"CNC001","AGV-01",80,"MAP-INTEGRATION","V1"){ParameterRevision=parameters.Revision},default));
            var secondRuntime=Await(client.CreateTaskAsync(new(Guid.NewGuid().ToString("N"),"CNC002","AGV-02",20,"MAP-INTEGRATION","V1"){ParameterRevision=parameters.Revision},default));
            Check(first.Accepted&&secondRuntime.Accepted,"工程绑定经真实管道创建两个工位任务");
            Cycle(ack,"ProcessPendingAcknowledgementsAsync");plc.Scan();
            dispatch.EngineeringMapVersion="OTHER";Cycle(orchestrator,"RunCycleAsync");
            Check(db.Queryable<AgvCommandOutboxEntity>().Count()==0,"地图版本变化阻止真实调度器派发");
            dispatch.EngineeringMapVersion="V1";
            dispatch.StationVehiclePools["CNC001"]=["AGV-NOT-AUTHORIZED"];dispatch.StationVehiclePools["CNC002"]=["AGV-NOT-AUTHORIZED"];
            Cycle(orchestrator,"RunCycleAsync");
            Check(db.Queryable<AgvCommandOutboxEntity>().Count()==0,"在线车辆仍不能越过工程车辆池");
            dispatch.StationVehiclePools["CNC001"]=["AGV-01"];dispatch.StationVehiclePools["CNC002"]=["AGV-02"];
            foreach(var item in new[]{(Id:first.TaskId!,Robot:robot1,Station:"CNC001"),(Id:secondRuntime.TaskId!,Robot:robot2,Station:"CNC002")})
            {
                Cycle(orchestrator,"RunCycleAsync");
                Move(0);plc.Set(item.Station,"BoxPresent",false);HandshakeRuntime();Move(2);
                plc.Set("DUMPER-001","DumperReady",true);HandshakeRuntime();Move(4);
                plc.Set("DUMPER-001","DumpingCompletedMayLeave",true);HandshakeRuntime();Move(6);
                plc.Set(item.Station,"BoxPresent",true);HandshakeRuntime();HandshakeRuntime();
                var detail=Await(client.GetTaskDetailAsync(item.Id,default))!;
                Check(detail.Task.Stage==DispatchTaskStage.Completed&&detail.Steps.All(s=>s.Status==DispatchTaskStepStatus.Completed),"真实九步执行器完成工程任务 "+item.Station);
                Check(detail.Commands.Count==4&&detail.Commands.All(c=>c.AttemptCount==1),"四段真实 Robokit 回环命令各发送一次 "+item.Station);
                void HandshakeRuntime() {for(int j=0;j<3;j++){Cycle(steps,"RunCycleAsync");plc.Scan();}}
                void Move(int index)
                {
                    Cycle(steps,"RunCycleAsync");Cycle(sender,"DispatchDueCommandsAsync");Poll();
                    var step=Await(tasks.GetStepsAsync(item.Id,default))[index];
                    Check(step.Status==DispatchTaskStepStatus.Executing&&item.Robot.Commands.ContainsKey(step.StepId),"工程车辆池对应车辆实际收到运输命令 "+item.Station+"/"+index);
                    item.Robot.Complete(step.StepId);Poll();Cycle(orchestrator,"RunCycleAsync");
                    Check(Await(tasks.GetStepsAsync(item.Id,default))[index].Status==DispatchTaskStepStatus.Completed,"回环完成反馈推进原运输步骤 "+index);
                }
            }
            Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==0,"工程任务正常结束无残留路权");
            File.WriteAllText(Path.Combine(root,"engineering-runtime-result.json"),JsonSerializer.Serialize(new{Checks=checks,First=first.TaskId,Second=secondRuntime.TaskId,Profile=profile,Robot1=robot1.Commands,Robot2=robot2.Commands,Scope="Actual service + SQLite + Named Pipe + loopback TCP; serial tasks; no field devices"},new JsonSerializerOptions{WriteIndented=true}));
            return;
            }
            var app=new AgvDispatch.Wpf.App(); app.InitializeComponent();
            vm=new TaskCenterViewModel(client); ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
            vm.OnNavigatedTo(null!); Pump(()=>!vm.IsBusy); Await(vm.LoadCreationOptionsAsync());
            if (!vm.CreationStations.Any() || !vm.CreationVehicles.Any(v=>v.Id=="AGV-01")) throw new InvalidOperationException("创建选项未就绪：" + vm.CreationMessage + "；工位=" + string.Join(",",vm.CreationStations.Select(s=>s.StationId)) + "；车辆=" + string.Join(",",vm.CreationVehicles.Select(v=>v.Id)));
            vm.CreationStation=vm.CreationStations.Single(s=>s.StationId=="CNC001"); vm.CreationVehicle=vm.CreationVehicles.Single(v=>v.Id=="AGV-01");
            Check(vm.CreationStepPreview.Count==9 && vm.CreationStepPreview.All(s=>!s.HasErrors),"真实IPC载入创建前九步预览，PLC及动作配置通过");
            Await(vm.PreviewCreationRoutesAsync());
            Check(vm.CreationMapRoutes.Count==4,"真实IPC返回同批四段导航几何");
            foreach(var sequence in new[]{1,3,5,7})
            {
                vm.SelectedCreationRouteSegment=vm.CreationRouteSegments.Single(s=>s.Sequence==sequence);
                Check(vm.CreationRouteActionDetails.Contains("随后第")&&vm.CreationRouteActionDetails.Contains("非实时信号"),"地图选段联动动作和后续PLC配置 第"+sequence+"步");
                var mapView=new TaskCreationRouteMapView{DataContext=vm,Width=1100,Height=650};
                mapView.Measure(new Size(1100,650));mapView.Arrange(new Rect(0,0,1100,650));mapView.UpdateLayout();
                var image=new RenderTargetBitmap(1100,650,96,96,PixelFormats.Pbgra32);image.Render(mapView);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(root,"preview-step-"+sequence+".png")))encoder.Save(file);
                timeline.Add(new{Kind="PreviewConfiguration",Step=sequence,Details=vm.CreationRouteActionDetails,Note="本模拟未配置车端地图核验，不把本地路线视为厂家已确认。"});
            }
            vm.CreationPriorityInput="80"; Await(vm.SubmitTaskAsync());
            Check(vm.Detail?.Task.StationId=="CNC001" && vm.Detail.Task.Priority==80,"前端提交命令经真实管道创建任务并读回详情");
            var a=vm.Detail!.Task.TaskId;
            var second=Await(client.CreateTaskAsync(new(Guid.NewGuid().ToString("N"),"CNC002","AGV-02",20,"MAP-INTEGRATION","V1"){ParameterRevision=parameters.Revision},default));
            Check(second.Accepted,"客户端经真实管道创建第二个独立工位任务");
            var b=second.TaskId!; vm.Search="";
            Cycle(ack,"ProcessPendingAcknowledgementsAsync"); plc.Scan();
            Cycle(orchestrator,"RunCycleAsync"); Cycle(sender,"DispatchDueCommandsAsync"); Poll();
            Check(robot1.Commands.Count==1 && robot2.Commands.Count==0,"高优先级任务取得共享资源后仅向第一辆模拟车下发");
            var waiting=Await(client.GetTrafficWaitingAsync(new(),default));
            Check(waiting.Items.Count==1 && waiting.Items[0].TaskId==b && waiting.Items[0].Blockers.Any(x=>x.TaskId==a && x.VehicleId=="AGV-01"),"第二辆车等待队列明确关联阻塞任务、车辆和共享资源");
            for(int i=0;i<3;i++) { Cycle(orchestrator,"RunCycleAsync"); Cycle(sender,"DispatchDueCommandsAsync"); }
            Check(robot1.Commands.Count==1 && robot2.Commands.Count==0,"重复调度不重复下发且冲突车辆不越权行驶");
            var repeated=Await(client.GetTrafficWaitingAsync(new(),default)).Items.Single();
            Check(repeated.WaitingSeconds>=0 && repeated.WaitingSince==waiting.Items.Single().WaitingSince,"重试保持等待起点与持续时间");
            Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==b),"等待任务未取得部分路权");
            Cycle(vm,"RefreshAsync"); vm.SelectedTask=vm.Tasks.Single(t=>t.TaskId==a); Pump(()=>vm.Detail?.Task.TaskId==a);
            Check(vm.Steps.Count==9 && vm.Commands.Count==1 && vm.Resources.Count==1,"前端真实IPC展示九步、发送记录与当前路权"); Render(app,vm,Path.Combine(root,"task-running.png"));

            CompleteNine(a,robot1,"CNC001");
            Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==a),"第一任务PLC收尾完成后正常释放全部路权");
            Cycle(orchestrator,"RunCycleAsync"); Cycle(sender,"DispatchDueCommandsAsync"); Poll();
            Check(robot2.Commands.Count==1 && !Await(client.GetTrafficWaitingAsync(new(),default)).Items.Any(i=>i.TaskId==b),"资源释放后第二车辆取得路权并退出等待");
            CompleteNine(b,robot2,"CNC002");
            Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==0,"两任务完成无残留路权");
            Check(robot1.Commands.Count==4 && robot2.Commands.Count==4,"每辆车只发送四段运输且每段编号唯一");
            Check(robot1.Commands.Values.Select(c=>c.Operation).SequenceEqual(new[]{"JackLoad","Wait","Wait","JackUnload"}),"真实3066报文包含四段对应动作");
            Check(plc.Pending.Count==0 && new[]{"CNC001","CNC002"}.All(s=>new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"}.All(n=>!plc.Value(s,n))),"PLC应答与收尾信号均已复位");
            Cycle(vm,"RefreshAsync"); vm.SelectedTask=vm.Tasks.Single(t=>t.TaskId==b); Pump(()=>vm.Detail?.Task.TaskId==b);
            Check(vm.Steps.All(s=>s.Status==DispatchTaskStepStatus.Completed),"前端最终显示九步全部完成"); Render(app,vm,Path.Combine(root,"task-completed.png"));
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new { Checks=checks,First=a,Second=b,Robot1=robot1.Commands,Robot2=robot2.Commands,PlcWrites=plc.Writes.Select(w=>new { w.Station,w.Name,w.Value }).ToArray(),Timeline=timeline,Scope="in-process real service components + Named Pipe + WPF + loopback Robokit; PLC simulated at gateway/cache boundary" },new JsonSerializerOptions{WriteIndented=true}));

            void CompleteNine(string taskId,SimulatedRobot robot,string station)
            {
                Transport(0); WaitStep(1);
                // Missing/stale PLC data cannot advance pickup even when its boolean looks satisfied.
                plc.Set(station,"BoxPresent",false,SignalQuality.Bad); Cycle(steps,"RunCycleAsync");
                Check(Await(tasks.GetStepsAsync(taskId,default))[1].Status==DispatchTaskStepStatus.Ready,"坏质量箱体信号阻止取箱步骤推进 "+station);Capture(2,"BoxPresent=false但Quality=Bad，等待有效质量");
                plc.Set(station,"BoxPresent",false); Handshake(); Transport(2);
                var pickupStepId=Await(tasks.GetStepsAsync(taskId,default))[0].StepId;
                Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId==pickupStepId),"后续运输命令登记后释放已结束首段路权 "+station);
                WaitStep(3);
                plc.Set("DUMPER-001","DumperReady",false); Handshake();
                Check(Await(tasks.GetStepsAsync(taskId,default))[3].Status==DispatchTaskStepStatus.Ready,"倒料Ready未满足保持等待 "+station);Capture(4,"DumperReady=false，保持等待");
                plc.Set("DUMPER-001","DumperReady",true); Handshake(); Transport(4);
                var deliveryStepId=Await(tasks.GetStepsAsync(taskId,default))[2].StepId;
                Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId==deliveryStepId),"进入倒料位前释放已结束送货段路权 "+station);
                WaitStep(5);
                plc.Set("DUMPER-001","DumpingCompletedMayLeave",false); Handshake();
                Check(Await(tasks.GetStepsAsync(taskId,default))[5].Status==DispatchTaskStepStatus.Ready,"未允许离开不跳过倒料步骤 "+station);Capture(6,"DumpingCompletedMayLeave=false，不离开倒料位");
                plc.Set("DUMPER-001","DumpingCompletedMayLeave",true); Handshake(); Transport(6); WaitStep(7);
                Handshake(); Check(Await(tasks.GetStepsAsync(taskId,default))[7].Status==DispatchTaskStepStatus.Ready,"空箱未检测到时不完成放箱 "+station);Capture(8,"BoxPresent=false，空箱未到位不收尾");
                plc.Set(station,"BoxPresent",true); Handshake(); Handshake();
                var completed=Await(client.GetTaskDetailAsync(taskId,default))!;
                Check(completed.Task.Stage==DispatchTaskStage.Completed && completed.Steps.All(s=>s.Status==DispatchTaskStepStatus.Completed),"九步任务经PLC条件闭环完成 "+station);
                Check(completed.Commands.Count==4 && completed.Commands.All(c=>c.AttemptCount==1),"每段运输仅发送一次 "+station);Capture(9,"PLC收尾复位、九步完成并释放资源");
                void Capture(int sequence,string cause)
                {
                    var detail=Await(client.GetTaskDetailAsync(taskId,default))!;
                    var row=detail.Steps.Single(s=>s.Sequence==sequence);
                    timeline.Add(new{Kind="Execution",TaskId=taskId,Station=station,Step=sequence,Reason=cause,Stage=detail.Task.Stage.ToString(),Status=row.Status.ToString(),Signals=plc.GetAll().Where(s=>s.StationId==station||s.StationId=="DUMPER-001").Select(s=>new{s.StationId,s.VariableName,s.Value,Quality=s.Quality.ToString()}).ToArray()});
                    if(taskId==a){Cycle(vm,"RefreshAsync");vm.SelectedTask=vm.Tasks.Single(t=>t.TaskId==taskId);Pump(()=>vm.Detail?.Task.TaskId==taskId);Render(app,vm,Path.Combine(root,"execution-step-"+sequence+".png"),sequence);}
                }
                void WaitStep(int index) => Check(Await(tasks.GetStepsAsync(taskId,default))[index].Status==DispatchTaskStepStatus.Ready,"仅激活下一步 "+(index+1)+" "+station);
                void Transport(int index)
                {
                    Cycle(steps,"RunCycleAsync"); Cycle(sender,"DispatchDueCommandsAsync"); Poll();
                    if(taskId==a) Check(robot2.Commands.Count==0 && db.Queryable<TrafficResourceLeaseEntity>().ToList().Select(l=>l.TaskId).Distinct().SequenceEqual(new[]{a}),"第一任务运输期间第二车辆持续等待且容量不超额 "+(index+1));
                    var current=Await(tasks.GetStepsAsync(taskId,default))[index];
                    Check(current.Status==DispatchTaskStepStatus.Executing && robot.Commands.ContainsKey(current.StepId),"运输步骤真正经TCP下发 "+(index+1)+" "+station);
                    var observedRoute=Await(client.GetTaskDetailAsync(taskId,default))!.Commands.Single(c=>c.StepId==current.StepId).Route;
                    Check(observedRoute is not null&&observedRoute.TaskId==taskId&&observedRoute.VehicleId==robot.Configuration.VehicleId&&observedRoute.MapId=="MAP-INTEGRATION"&&observedRoute.EdgeIds.Length==observedRoute.NodeIds.Length-1,"真实IPC任务详情带原运输快照与动作 "+(index+1)+" "+station);
                    robot.Complete(current.StepId); Poll(); Cycle(orchestrator,"RunCycleAsync");
                    Check(Await(tasks.GetStepsAsync(taskId,default))[index].Status==DispatchTaskStepStatus.Completed,"1110完成反馈推进运输步骤 "+(index+1)+" "+station);Capture(index+1,"1110确认完成；真实3066模拟报文动作="+robot.Commands[current.StepId].Operation);
                }
            }
            void Handshake() { for(int i=0;i<3;i++) { Cycle(steps,"RunCycleAsync"); plc.Scan(); } }
        }
        finally { vm?.OnNavigatedFrom(null!); Await(server.StopAsync(default)); Await(manager.DisposeAsync().AsTask()); }
        void Poll() { foreach(var robot in manager.Clients) { var sample=Await(robot.PollAsync(default)); states.ApplyTelemetry(robot.Robot.VehicleId,sample.Telemetry,sample.ResponseTime); } }
    }
    static void Render(AgvDispatch.Wpf.App app,TaskCenterViewModel vm,string file,int sequence=0)
    {
        var view=new TaskCenterView { DataContext=vm,Width=1280,Height=800,Background=(Brush)app.FindResource("AppBackgroundBrush") };
        view.Measure(new Size(1280,800)); view.Arrange(new Rect(0,0,1280,800)); view.UpdateLayout();
        if(sequence>0)
        {
            var timeline=Children(view).OfType<System.Windows.Controls.ItemsControl>().First(c=>ReferenceEquals(c.ItemsSource,vm.Steps));
            var scroll=(System.Windows.Controls.ScrollViewer)view.FindName("TaskTimelineScroll");
            if(timeline.ItemContainerGenerator.ContainerFromIndex(sequence-1) is FrameworkElement container){var position=container.TransformToAncestor(timeline).Transform(new Point());scroll.ScrollToVerticalOffset(Math.Max(0,position.Y));view.UpdateLayout();}
        }
        var image=new RenderTargetBitmap(1280,800,96,96,PixelFormats.Pbgra32); image.Render(view);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream=File.Create(file); png.Save(stream);
    }
    static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}}
}

sealed class EmptyFlow : ITrafficFlowMonitor
{
    public TrafficFlowSnapshot GetSnapshot()=>new(DateTimeOffset.Now,"MAP-INTEGRATION","V1",[],[]);
    public IReadOnlyDictionary<string,double> GetEdgePenalties()=>new Dictionary<string,double>();
}
sealed class SimulatedStations(SimulatedPlc plc) : IStationStateService
{
    public event Action<StationStateChanged>? StationStateChanged { add{} remove{} } public long Version=>plc.Version;
    public IReadOnlyList<StationStateSnapshot> GetAll()=>[];
    public bool TryGet(string id,out StationStateSnapshot snapshot)
    { snapshot=new(id,default,true,null,true,true,false,true,SignalQuality.Good,null,0,DateTimeOffset.Now,DateTimeOffset.Now,Version); return true; }
}
sealed class SimulatedPlc : IPlcGateway,IPlcSignalCache,IPlcConfigurationInspector
{
    readonly Dictionary<(string,string),PlcSignalSnapshot> signals=[];
    public List<(string Station,string Name,bool Value)> Pending=[]; public List<(string Station,string Name,bool Value)> Writes=[];
    readonly List<PlcSignalDefinition> definitions=[];
    public SimulatedPlc()
    {
        foreach(var station in new[]{"CNC001","CNC002","DUMPER-001"})
        foreach(var name in station.StartsWith("CNC") ? new[]{"FullBoxRequested","BoxPresent","Faulted","CommunicationOnline","FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"} : new[]{"Faulted","DumperReady","AgvAtWaitingPosition","AgvAtDumpingPosition","DumpingCompletedMayLeave"})
        {
            var write=new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced","AgvAtWaitingPosition","AgvAtDumpingPosition"}.Contains(name);
            var address="DB1.DBX"+definitions.Count+".0";
            definitions.Add(new(address,name,name,station,"simulation",0,"Bool","",write?"Write":"Read","Confirmed",true,"","","isolated test"));
            signals[(station,name)]=new(station,name,name,address,"Bool","",write?"Write":"Read",true,false,SignalQuality.Good,DateTimeOffset.Now,DateTimeOffset.Now,0);
            Set(station,name,name is "BoxPresent" or "CommunicationOnline" or "FullBoxRequested");
        }
    }
    public void Set(string s,string n,bool value,SignalQuality quality=SignalQuality.Good) { signals[(s,n)]=signals[(s,n)] with { Value=value,Quality=quality,Version=++Version,UpdatedAt=DateTimeOffset.Now }; }
    public bool Value(string s,string n)=>signals[(s,n)].Value==true;
    public void Scan() { foreach(var p in Pending) Set(p.Station,p.Name,p.Value); Pending.Clear(); }
    public event Action<PlcSignalChanged>? SignalChanged { add{} remove{} } public long Version { get; private set; }
    public IReadOnlyList<PlcSignalSnapshot> GetAll()=>signals.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string s)=>signals.Values.Where(p=>p.StationId==s).ToArray();
    public bool TryGet(string s,string n,out PlcSignalSnapshot p)=>signals.TryGetValue((s,n),out p!);
    public Task WriteSignalAsync(string s,string n,bool v,CancellationToken ct) { ct.ThrowIfCancellationRequested(); if(GetSignalConfigurationError(s,n,true)!=null) throw new Exception("Invalid simulated write"); Pending.Add((s,n,v)); Writes.Add((s,n,v)); return Task.CompletedTask; }
    public Task<bool?> ReadSignalAsync(string s,string n,CancellationToken ct)=>Task.FromResult(signals[(s,n)].Value);
    public Task<StationSignal?> ReadStationAsync(string s,CancellationToken ct)=>Task.FromResult<StationSignal?>(null);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>Task.FromResult(new DeviceConnectionStatus("SIM-PLC","模拟PLC",ConnectionState.Online,DateTimeOffset.Now));
    public string? GetSignalConfigurationError(string s,string n,bool write)=>PlcTaskConfigurationInspector.Inspect(new(){Enabled=true,AllowWrites=true},PlcSignalCatalog.FromSignals(definitions),s,n,write);
}
sealed class SimulatedRobot : IDisposable
{
    public sealed record Command(string Source,string Destination,string Operation);
    readonly TcpListener listener=new(IPAddress.Loopback,0); readonly CancellationTokenSource stop=new(); readonly Task server;
    readonly IReadOnlyList<MapNode> nodes; readonly Dictionary<string,int> statuses=[];
    public Dictionary<string,Command> Commands { get; }=[]; string station;
    public AgvRobotOptions Configuration { get; }
    public SimulatedRobot(string id,string home,IReadOnlyList<MapNode> nodes)
    { this.nodes=nodes; station=home; listener.Start(); Configuration=new(){VehicleId=id,IpAddress="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port}; Configuration.NavigationPort=Configuration.Port; server=Task.Run(Serve); }
    public void Complete(string id) { lock(statuses) { statuses[id]=4; station=Commands[id].Destination; } }
    async Task Serve()
    {
        try
        {
            using var client=await listener.AcceptTcpClientAsync(stop.Token); using var stream=client.GetStream();
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16]; await stream.ReadExactlyAsync(header,stop.Token);
                var length=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4)); if(length<0 || length>1048576) throw new Exception("Mock frame length");
                var data=new byte[length]; await stream.ReadExactlyAsync(data,stop.Token); using var request=JsonDocument.Parse(length==0?"{}":System.Text.Encoding.UTF8.GetString(data));
                var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2)); object response;
                lock(statuses)
                {
                    if(type==1100) { var node=nodes.Single(n=>n.Id==station); response=new { x=node.X,y=node.Y,angle=0,confidence=1,current_station=station,battery_level=0.9,blocked=false,emergency=false }; }
                    else if(type==1110)
                    {
                        var ids=request.RootElement.TryGetProperty("task_ids",out var list)?list.EnumerateArray().Select(x=>x.GetString()!).ToArray():statuses.Keys.ToArray();
                        response=new { task_status_list=ids.Select(id=>new {task_id=id,status=statuses.GetValueOrDefault(id,404)}).ToArray() };
                    }
                    else if(type==3066)
                    {
                        var list=request.RootElement.GetProperty("move_task_list").EnumerateArray().ToArray(); var last=list[^1]; var id=last.GetProperty("task_id").GetString()!;
                        if(Commands.ContainsKey(id)) throw new Exception("Duplicate command "+id);
                        Commands[id]=new(list[0].GetProperty("source_id").GetString()!,last.GetProperty("id").GetString()!,last.GetProperty("operation").GetString()!); statuses[id]=2; response=new{ret_code=0};
                    }
                    else throw new Exception("Unexpected mock protocol "+type);
                }
                var payload=JsonSerializer.SerializeToUtf8Bytes(response); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),payload.Length); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                await stream.WriteAsync(header,stop.Token); await stream.WriteAsync(payload,stop.Token); await stream.FlushAsync(stop.Token);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or EndOfStreamException or ObjectDisposedException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); try{server.GetAwaiter().GetResult();}finally{stop.Dispose();} }
}
