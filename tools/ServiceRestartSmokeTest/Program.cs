using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    static int checks;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    [STAThread] static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try {Run();Console.WriteLine($"PASS: {checks} independent-process restart checks; private deployment, pipe and SQLite only.");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void Pump(Func<bool> done)
    {
        if(done())return;var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(35);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
        timer.Tick+=(_,_)=>{if(done()||DateTime.UtcNow>deadline)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();if(!done())throw new TimeoutException("isolated process check timed out");
    }
    static void Await(Task t){Pump(()=>t.IsCompleted);t.GetAwaiter().GetResult();}
    static T Await<T>(Task<T> t){Pump(()=>t.IsCompleted);return t.GetAwaiter().GetResult();}
    static void Run()
    {
        var source=Path.GetFullPath("AgvDispatch.Service/bin/Debug/net10.0-windows10.0.19041.0/win-x64");
        if(!File.Exists(Path.Combine(source,"AgvDispatch.Service.exe")))throw new Exception("Run from DispatchingSystem after building the test");
        var root=Path.Combine(Path.GetTempPath(),"agv-process-restart-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Console.WriteLine("Evidence: "+root);
        var deploy=Path.Combine(root,"host");Directory.CreateDirectory(deploy);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(source,file);if(relative.StartsWith("data"+Path.DirectorySeparatorChar)||relative.StartsWith("logs"+Path.DirectorySeparatorChar))continue;
            var target=Path.Combine(deploy,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
        }
        var pipe="AgvRestart-"+Guid.NewGuid().ToString("N");var dbPath=Path.Combine(root,"test.db");
        var config=new
        {
            Database=new{Path=dbPath},Security=new{PipeName=pipe},
            Plc=new{Enabled=false,AllowWrites=false,SignalCsvPath="config/plc-signals.csv"},
            Agv=new{Enabled=false,AllowCommands=false,Robots=Array.Empty<object>()},Map=new{Enabled=false},
            TaskCreation=new{Enabled=true,DumperStationId="DUMPER-SIM"},
            Dispatching=new{Enabled=true,DumperWaitingSite="WAIT",DumperDumpingSite="DUMP"},
            AgvCommandOutbox=new{Enabled=true},DispatchTaskSteps=new{Enabled=true},
            ExecutionIdentityAcquisition=new{Enabled=false},RecoveryIdentity=new{RequireMachineIdentity=false}
        };
        File.WriteAllText(Path.Combine(deploy,"appsettings.json"),JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));
        var catalog=new TrafficResourceCatalog([new("R-RUN",TrafficResourceType.NarrowAisle),new("R-SEND",TrafficResourceType.Intersection)]);
        string runningId,sendingId,runStep,sendStep,sendCommand;string[] leaseIds;
        using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath}))
        {
            Await(new DatabaseInitializer(db,catalog).InitializeAsync(default));var tasks=new DispatchTaskRepository(db);var outbox=new AgvCommandOutboxRepository(db);var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(60)));
            runningId=Create("CNC-RUN","R-RUN",true,out runStep,out _);sendingId=Create("CNC-SEND","R-SEND",false,out sendStep,out sendCommand);
            leaseIds=db.Queryable<TrafficResourceLeaseEntity>().ToList().Select(l=>l.LeaseId).Order().ToArray();
            db.Insertable(new MapTransferEntity{OperationId="MAP-UNKNOWN",RequestId="map-unknown-request",IntentHash="simulated",MapId="SIM-MAP",VersionId="SIM-V1",VehicleId="SIM-CNC-RUN",State="Sending",AttemptCount=1,OwnerSession="before-restart",CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow}).ExecuteCommand();
            string Create(string station,string resource,bool sent,out string stepId,out string commandId)
            {
                var created=Await(tasks.TryCreateAsync(station,"DUMPER-SIM",20,default));Await(tasks.MarkAcknowledgedAsync(created.Task.TaskId,default));stepId=Await(tasks.GetStepsAsync(created.Task.TaskId,default))[0].StepId;
                Await(traffic.RecordRouteAsync(new(stepId,created.Task.TaskId,"SIM-"+station,"HOME","PICK",["EDGE"],[resource],false,"restart fixture"),default));Await(traffic.TryAcquireAsync(stepId,created.Task.TaskId,"SIM-"+station,[resource],default));
                Await(outbox.TryPrepareTransportStepAsync(created.Task.TaskId,stepId,"SIM-"+station,["HOME","PICK"],new("Wait"),default));var command=Await(outbox.GetDueCommandsAsync(DateTimeOffset.Now.AddSeconds(1),10,default)).Single();commandId=command.CommandId;Await(outbox.TryClaimAsync(commandId,DateTimeOffset.Now.AddSeconds(1),default));if(sent)Await(outbox.MarkSentAsync(commandId,stepId,default));else Await(db.Updateable<AgvCommandOutboxEntity>().SetColumns(c=>c.ClaimedAt==DateTime.Now.AddMinutes(-5)).Where(c=>c.CommandId==command.CommandId).ExecuteCommandAsync());return created.Task.TaskId;
            }
        }
        var app=new AgvDispatch.Wpf.App();app.InitializeComponent();
        using var client=new NamedPipeTrafficControlClient(pipe);var vm=new TaskCenterViewModel(client);ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
        var exe=Path.Combine(deploy,"AgvDispatch.Service.exe");RecoveryStatus? first=null;int firstPid=0;
        try
        {
            for(int boot=1;boot<=2;boot++)
            {
                using var host=new TestHost(exe,deploy,dbPath,pipe,root,boot);var recovery=Await(WaitForHost(client,host.Process));var status=Await(client.GetSystemStatusAsync(default));
                Check(status.Service.ProcessId==host.Process.Id && status.Service.ProcessId!=Environment.ProcessId,"真实后台独立进程与诊断PID一致 boot"+boot);
                Check(!status.Safety.AgvCommunicationEnabled&&!status.Safety.AgvCommandsAllowed&&!status.Safety.PlcCommunicationEnabled&&!status.Safety.PlcWritesAllowed,"实际进程PLC/AGV采集和写入均关闭 boot"+boot);
                var inspection=Await(client.GetRobokitInspectionAsync(new("SIM-CNC-RUN"),default));
                Check(!inspection.Available && inspection.Stations.Count==0 && inspection.Reason.Contains("未启用"),"真实独立后台只读核验IPC保持现场采集关闭 boot"+boot);
                var history=Await(client.GetRobokitInspectionHistoryAsync(new("SIM-CNC-RUN"),default));
                Check(history.Count==boot && history.All(r=>!r.Available && r.VehicleId=="SIM-CNC-RUN" && !string.IsNullOrWhiteSpace(r.Operator)),"手动核验记录通过真实IPC持久化并跨进程重启保留 boot"+boot);
                var mapTransfer=Await(client.GetMapTransferAsync(new("MAP-UNKNOWN"),default));
                Check(mapTransfer.State=="Uncertain"&&mapTransfer.PreviousSession&&mapTransfer.AttemptCount==1&&!mapTransfer.WritesEnabled,"真实重启保留地图原写入次数/未知结果且无写入入口 boot"+boot);
                var mapProgress=Await(client.ObserveMapTransferAsync(new("MAP-UNKNOWN"),default));
                Check(mapProgress.State=="Uncertain"&&!mapProgress.GatesClear&&mapProgress.LastObservation.Contains("false"),"真实IPC只读复核不关闭地图未知状态 boot"+boot);
                Check(mapProgress.Events.Count(e=>e.Kind=="ServiceRestart")==boot&&mapProgress.Events.Count(e=>e.Kind=="ReadOnlyObservation")==boot,"地图申请历史跨独立进程保留 boot"+boot);
                var transferPage=Await(client.ListMapTransfersAsync(new("SIM-CNC-RUN",State:"Uncertain"),default));
                Check(transferPage.Total==1&&transferPage.Items.Single().OperationId=="MAP-UNKNOWN","真实IPC列表筛选保留原操作 boot"+boot);
                var transferHistory=Await(client.GetMapTransferHistoryAsync(new("MAP-UNKNOWN"),default));
                Check(transferHistory.Total==boot*2&&transferHistory.Items.Count==boot*2,"真实IPC独立历史分页跨重启保留 boot"+boot);
                var transferReview=Await(client.GetMapTransferReviewAsync(new("MAP-UNKNOWN"),default));
                Check(transferReview.RequiresDisposition&&!transferReview.CanClose&&!transferReview.CanResend&&!transferReview.CanRelease&&transferReview.Record.AttemptCount==1,"真实IPC处置契约不授予关闭/补发/放行 boot"+boot);
                Check(Await(client.GetMapSwitchAsync(default)).Mode==MapExecutionMode.Held,"真实后台地图执行门保持Held boot"+boot);
                Check(status.Safety.DispatchingEnabled,"后台调度循环启用以验证恢复屏障 boot"+boot);
                Check(!recovery.Released&&recovery.Items.Any(i=>i.TaskId==runningId)&&recovery.Items.Any(i=>i.TaskId==sendingId),"实际启动恢复创建运行与发送不明核对条目 boot"+boot);
                Check(recovery.Items.All(i=>!i.Verified),"无设备证据不得自动核对运行任务 boot"+boot);
                var run=Await(client.GetTaskDetailAsync(runningId,default))!;var send=Await(client.GetTaskDetailAsync(sendingId,default))!;
                Check(run.Steps[0].StepId==runStep&&run.Steps[0].VendorTaskId==runStep&&run.Steps[0].Status==DispatchTaskStepStatus.Executing,"重启读取原运输编号与执行步骤 boot"+boot);
                Check(send.Steps[0].StepId==sendStep&&send.Commands.Single().CommandId==sendCommand&&send.Commands.Single().AttemptCount==1,"发送不明命令保持原编号与次数 boot"+boot);
                Check(Await(client.GetResourcesAsync(default)).SelectMany(r=>r.Leases).Select(r=>r.LeaseId).Order().SequenceEqual(leaseIds),"原路权编号与占用保留 boot"+boot);
                var released=Await(client.ReleaseExecutionRecoveryAsync(new(recovery.SessionId,"模拟请求核验恢复屏障拒绝未完成现场确认"),default));
                Check(!released.Accepted,"未确认条目不能通过恢复放行接口 boot"+boot);
                Await(Task.Delay(3500)); // Allow hosted loops, including stale-claim polling, to execute.
                var later=Await(client.GetExecutionRecoveryAsync(default));var laterSend=Await(client.GetTaskDetailAsync(sendingId,default))!;
                Check(!later.Released&&laterSend.Commands.Single().AttemptCount==1&&laterSend.Commands.Single().Status==AgvCommandStatus.Sending,"真实后台循环不回收/重发屏障内的发送不明命令 boot"+boot);
                vm.OnNavigatedTo(null!);Pump(()=>!vm.IsBusy&&vm.Tasks.Count==2);vm.SelectedTask=vm.Tasks.Single(t=>t.TaskId==runningId);Pump(()=>vm.Detail?.Task.TaskId==runningId);
                Check(vm.Steps.Count==9&&vm.Resources.Count==1,"独立前端组件通过真实管道显示九步与原占用 boot"+boot);Render(app,vm,Path.Combine(root,"task-boot"+boot+".png"));vm.OnNavigatedFrom(null!);
                if(boot==1){first=recovery;firstPid=host.Process.Id;}
                else{Check(recovery.SessionId!=first!.SessionId&&host.Process.Id!=firstPid,"真正进程重启建立新恢复批次");Check(recovery.Items.All(i=>!i.Verified),"上次启动未完成核对不会跨进程放行");}
                File.WriteAllText(Path.Combine(root,"boot"+boot+".json"),JsonSerializer.Serialize(new{ProcessId=host.Process.Id,recovery.SessionId,recovery.Released,Items=recovery.Items,Resources=Await(client.GetResourcesAsync(default)),Commands=laterSend.Commands},new JsonSerializerOptions{WriteIndented=true}));
            }
            using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});Check(db.Queryable<ExecutionRecoverySessionEntity>().Count()==2,"两次真实进程启动保存两份恢复批次");
            Check(db.Queryable<MapTransferEntity>().Where(x=>x.OperationId=="MAP-UNKNOWN").First().AttemptCount==1,"两次进程重启从不增加地图写入次数");
            Check(db.Queryable<TrafficResourceLeaseEntity>().ToList().Select(l=>l.LeaseId).Order().SequenceEqual(leaseIds),"进程停止后数据库仍保留全部原路权");
        }
        finally{vm.OnNavigatedFrom(null!);}
    }
    static async Task<RecoveryStatus> WaitForHost(NamedPipeTrafficControlClient client,Process host)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));Exception? last=null;
        while(!timeout.IsCancellationRequested)
        {
            if(host.HasExited)throw new Exception("Isolated host exited with "+host.ExitCode);
            try{return await client.GetExecutionRecoveryAsync(timeout.Token);}catch(Exception e)when(e is not OperationCanceledException){last=e;}
            await Task.Delay(100,timeout.Token);
        }
        throw new TimeoutException("Isolated host not ready",last);
    }
    static void Render(AgvDispatch.Wpf.App app,TaskCenterViewModel vm,string file)
    {
        var view=new TaskCenterView{DataContext=vm,Width=1280,Height=800,Background=(Brush)app.FindResource("AppBackgroundBrush")};view.Measure(new Size(1280,800));view.Arrange(new Rect(0,0,1280,800));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1280,800,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(file);png.Save(stream);
    }
    sealed class TestHost : IDisposable
    {
        public Process Process {get;} readonly Task<string> stdout,stderr;readonly string root;readonly int boot;
        public TestHost(string exe,string directory,string database,string pipe,string root,int boot)
        {
            this.root=root;this.boot=boot;var info=new ProcessStartInfo(exe){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            info.Environment["DOTNET_ENVIRONMENT"]="Production";info.Environment["AGV_DISPATCH_DATABASE_PATH"]=database;
            foreach(var arg in new[]{"--Database:Path="+database,"--Security:PipeName="+pipe,"--Agv:Enabled=false","--Agv:AllowCommands=false","--Plc:Enabled=false","--Plc:AllowWrites=false","--ExecutionIdentityAcquisition:Enabled=false","--RecoveryIdentity:RequireMachineIdentity=false","--Map:Enabled=false"})info.ArgumentList.Add(arg);
            Process=Process.Start(info)!;stdout=Process.StandardOutput.ReadToEndAsync();stderr=Process.StandardError.ReadToEndAsync();
        }
        public void Dispose()
        {
            try{if(!Process.HasExited){Process.Kill();Process.WaitForExit(10000);}File.WriteAllText(Path.Combine(root,"stdout-boot"+boot+".log"),Await(stdout));File.WriteAllText(Path.Combine(root,"stderr-boot"+boot+".log"),Await(stderr));}
            finally{Process.Dispose();}
        }
    }
}


