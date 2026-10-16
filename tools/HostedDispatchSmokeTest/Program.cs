using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    static int checks;
    [STAThread] static int Main(string[] args)
    {
        if(args.Length==3 && args[0]=="--test-backend")
        {
            try{TestBackend.Run(args[1],args[2]).GetAwaiter().GetResult();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try{Run();Console.WriteLine($"PASS: {checks} hosted dispatch checks; independent test backend, real hosted cycles, loopback TCP and simulated PLC.");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static void Pump(Func<bool> done)
    {
        if(done())return;var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(120);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
        timer.Tick+=(_,_)=>{if(done()||DateTime.UtcNow>deadline)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();if(!done())throw new TimeoutException("Hosted test timed out");
    }
    static void Await(Task task){Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static T Await<T>(Task<T> task){Pump(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    static async Task<T> Until<T>(Func<Task<T>> read,Func<T,bool> done,Process process)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while(true)
        {
            if(process.HasExited)throw new Exception("Test backend exited: "+process.ExitCode);
            var result=await read();if(done(result))return result;await Task.Delay(100,timeout.Token);
        }
    }
    static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"agv-hosted-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Console.WriteLine("Evidence: "+root);
        var pipe="AgvHosted-"+Guid.NewGuid().ToString("N");
        var info=new ProcessStartInfo(Environment.ProcessPath!){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("--test-backend");info.ArgumentList.Add(root);info.ArgumentList.Add(pipe);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var client=new NamedPipeTrafficControlClient(pipe);TaskCenterViewModel? vm=null;
        try
        {
            Await(Until(()=>Task.FromResult(File.Exists(Path.Combine(root,"ready.json"))),v=>v,process));
            using var ready=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"ready.json")));
            Check(ready.RootElement.GetProperty("ProcessId").GetInt32()==process.Id&&process.Id!=Environment.ProcessId,"前端与后台为真实独立进程");
            Check(ready.RootElement.GetProperty("Robots").EnumerateArray().All(r=>r.GetProperty("IpAddress").GetString()=="127.0.0.1"),"模拟车只连接本机随机端口");
            var options=Await(Until(()=>client.GetTaskCreationOptionsAsync(default),o=>o.Available&&o.Vehicles.Count(v=>v.Available)==2,process));
            Check(options.Stations.Count==2&&options.Stations.All(s=>s.ConfigurationValid),"连续采集完成后两工位及两车可用");
            var app=new AgvDispatch.Wpf.App();app.InitializeComponent();vm=new TaskCenterViewModel(client);ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);vm.OnNavigatedTo(null!);Await(vm.LoadCreationOptionsAsync());
            vm.CreationStation=vm.CreationStations.Single(s=>s.StationId=="CNC001");vm.CreationVehicle=vm.CreationVehicles.Single(v=>v.Id=="AGV-01");vm.CreationPriorityInput="80";
            Check(vm.CreationStepPreview.Count==9&&vm.CreationStepPreview.All(s=>!s.HasErrors),"独立进程九步预览校验通过");Await(vm.SubmitTaskAsync());
            Check(vm.Detail?.Task.StationId=="CNC001","前端实际提交与详情读回");var first=vm.Detail!.Task.TaskId;
            var second=Await(client.CreateTaskAsync(new(Guid.NewGuid().ToString("N"),"CNC002","AGV-02",20,options.MapId!,options.MapVersionId!){ParameterRevision=options.ParameterRevision},default));Check(second.Accepted,"第二工位任务经真实IPC提交");
            var waiting=Await(Until(()=>client.GetTrafficWaitingAsync(new(),default),p=>p.Items.Any(i=>i.TaskId==second.TaskId),process));
            var blocked=waiting.Items.Single(i=>i.TaskId==second.TaskId);
            Check(blocked.Blockers.Any(b=>b.TaskId==first&&b.VehicleId=="AGV-01"),"真实后台周期产生两车冲突与阻塞关联");
            Check((Await(client.GetTaskDetailAsync(second.TaskId!,default))!.Commands).Count==0,"冲突车辆等待期间未产生运输命令");
            File.WriteAllText(Path.Combine(root,"waiting.json"),JsonSerializer.Serialize(waiting,new JsonSerializerOptions{WriteIndented=true}));
            Await(Until(()=>client.GetTaskDetailAsync(second.TaskId!,default),d=>d?.Task.Stage==DispatchTaskStage.Completed,process));
            var a=Await(client.GetTaskDetailAsync(first,default))!;var b=Await(client.GetTaskDetailAsync(second.TaskId!,default))!;
            Check(a.Task.Stage==DispatchTaskStage.Completed&&b.Task.Stage==DispatchTaskStage.Completed,"两任务由后台连续周期完成");
            Check(a.Steps.Count==9&&b.Steps.Count==9&&a.Steps.All(s=>s.Status==DispatchTaskStepStatus.Completed)&&b.Steps.All(s=>s.Status==DispatchTaskStepStatus.Completed),"两车完整九步均完成");
            Check(a.Commands.Count==4&&b.Commands.Count==4&&a.Commands.Concat(b.Commands).All(c=>c.AttemptCount==1),"每车四段运输仅发送一次");
            Check(Await(client.GetResourcesAsync(default)).All(r=>r.OccupiedCount==0),"PLC收尾后共享路权全部释放");
            Check(Await(client.GetTrafficWaitingAsync(new(),default)).TotalWaiting==0,"完成后等待队列为空");
            Await(Until(()=>Task.FromResult(File.Exists(Path.Combine(root,"devices.json"))),v=>v,process));
            using var devices=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"devices.json")));
            Check(devices.RootElement.GetProperty("First").EnumerateObject().Count()==4&&devices.RootElement.GetProperty("Second").EnumerateObject().Count()==4,"真实3066模拟端每车只接受四个唯一步骤");
            Check(devices.RootElement.GetProperty("Inputs").EnumerateArray().Where(s=>s.GetProperty("Access").GetString()=="Write").All(s=>!s.GetProperty("Value").GetBoolean()),"连续PLC扫描最终复位全部握手输出");
            vm.Search="";Await((Task)typeof(TaskCenterViewModel).GetMethod("RefreshAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[])!);vm.SelectedTask=vm.Tasks.Single(t=>t.TaskId==second.TaskId);Pump(()=>vm.Detail?.Task.TaskId==second.TaskId);
            Check(vm.Steps.All(s=>s.Status==DispatchTaskStepStatus.Completed)&&vm.Resources.Count==0,"独立前端最终显示完整结束与无占用");Render(app,vm,Path.Combine(root,"completed.png"));
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{Checks=checks,FrontendProcessId=Environment.ProcessId,BackendProcessId=process.Id,First=a,Second=b},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            vm?.OnNavigatedFrom(null!);File.WriteAllText(Path.Combine(root,"stop.request"),"stop test host only");
            if(!process.WaitForExit(15000)){process.Kill();process.WaitForExit(10000);}
            File.WriteAllText(Path.Combine(root,"stdout.log"),Await(stdout));File.WriteAllText(Path.Combine(root,"stderr.log"),Await(stderr));
        }
        Check(process.ExitCode==0,"后台协调器正常停止且子进程退出成功");
        Check(!File.ReadAllText(Path.Combine(root,"stdout.log")).Contains("fail:",StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(root,"stderr.log"))),"连续后台周期无数据库并发错误或未处理异常");
        var result=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root,"result.json")))!;result["Checks"]=checks;File.WriteAllText(Path.Combine(root,"result.json"),result.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
    }
    static void Render(AgvDispatch.Wpf.App app,TaskCenterViewModel vm,string file)
    {
        var view=new TaskCenterView{DataContext=vm,Width=1280,Height=800,Background=(Brush)app.FindResource("AppBackgroundBrush")};view.Measure(new Size(1280,800));view.Arrange(new Rect(0,0,1280,800));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1280,800,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(file);png.Save(stream);
    }
}
