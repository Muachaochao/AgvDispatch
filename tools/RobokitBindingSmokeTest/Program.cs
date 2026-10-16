using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Service;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Mvvm;

public static class Program
{
    static int checks;
    static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);checks++;Console.WriteLine("PASS "+reason);}
    static void Pump(Func<bool> done){if(done())return;var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};var end=DateTime.UtcNow.AddSeconds(15);timer.Tick+=(_,_)=>{if(done()||DateTime.UtcNow>end)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();if(!done())throw new TimeoutException();}
    static T Await<T>(Task<T> t){Pump(()=>t.IsCompleted);return t.GetAwaiter().GetResult();}
    [STAThread] public static int Main(){try{Run();Console.WriteLine($"PASS: {checks} map binding/history checks");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    static void Run()
    {
        var app=new System.Windows.Application();
        foreach(var uri in new[]{"HandyControl;component/Themes/SkinDefault.xaml","HandyControl;component/Themes/Theme.xaml","AgvDispatch.Wpf;component/Themes/Light.xaml","AgvDispatch.Wpf;component/Themes/HandyPalette.xaml","AgvDispatch.Wpf;component/Styles/Typography.xaml","AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml","AgvDispatch.Wpf;component/Styles/Controls.xaml","AgvDispatch.Wpf;component/Styles/HandyControls.xaml"})
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/"+uri)});
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var root=Path.Combine(Path.GetTempPath(),"agv-binding-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Console.WriteLine(root);
        using var fleet=new PlcFleet(new[]{new PlcOptions{DeviceId="PLC",Enabled=false,SignalCsvPath=""}});
        var options=new AgvOptions{Robots=[new(){VehicleId="AGV-01",Enabled=false}],MapBindings=[new(){VehicleId="AGV-01",LocalMapId="OLD",LocalMapVersion="V0",RobotMap="legacy",RobotMapMd5=new string('a',32),CoordinatesVerified=true}]};
        var config=CommunicationSettingsService.FromLegacy(new(){DeviceId="PLC",SignalCsvPath=""},options);
        var map=new ActiveMapRuntime();map.Replace(new("MAP","V1",new MapDefinition("test","","world","m",[],[],[]),DateTimeOffset.Now));
        var path=Path.Combine(root,"communication-settings.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new CommunicationSettingsDocument(1,"old",new(){Plcs=config.Plcs,Agvs=config.Agvs},[])));
        var service=new CommunicationSettingsService(path,root,config,fleet,new Tasks(),NullLogger<CommunicationSettingsService>.Instance,map);
        var user=TrafficUserContext.LocalOperation("sim-operator");
        var current=Await(service.GetAsync(user,default));
        Check(current.Saved.MapBindings?.Count==1,"旧保存文件缺字段继承原appsettings绑定");
        Check(current.Saved.MapBindings![0].CoordinatesVerified && current.Saved.MapBindings[0].ConfirmationNote=="","旧确认声明保留且不伪造核对依据");
        var client=DispatchProxy.Create<ITrafficControlClient,Proxy>();var proxy=(Proxy)(object)client;proxy.Service=service;proxy.User=user;
        var vm=new ConnectionSettingsViewModel(client);vm.OnNavigatedTo(null!);Pump(()=>!vm.IsBusy);
        Check(vm.MapBindings.Count==1 && vm.RuntimeMapText.Contains("V1"),"界面显示现有绑定和当前版本");
        vm.AddMapBindingCommand.Execute();var draft=vm.SelectedMapBinding!;
        Check(draft.VehicleId=="AGV-01" && draft.LocalMapId=="MAP" && draft.LocalMapVersion=="V1" && !draft.CoordinatesVerified,"新增自动带入车辆与版本但不自动确认坐标");
        draft.RobotMap="robot-demo";draft.RobotMapMd5=new string('b',32);draft.CoordinatesVerified=true;
        vm.SaveCommand.Execute();Pump(()=>!vm.IsBusy);
        Check(vm.Message.Contains("依据") && CommunicationSettingsService.ReadDocument(path)!.Revision=="old","无核对依据不能保存坐标确认");
        draft.ConfirmationNote="模拟场景坐标单位、原点和方向已逐项核对，2026-10-14";
        vm.SaveCommand.Execute();Pump(()=>!vm.IsBusy);
        Check(vm.Snapshot!.Saved.MapBindings!.Count==2 && vm.Snapshot.Active.MapBindings!.Count==1 && vm.Snapshot.RestartRequired,"保存成功但运行门禁配置不热更新");
        var persisted=CommunicationSettingsService.ReadDocument(path)!;
        var next=new AgvOptions();CommunicationSettingsService.ApplyAgv(persisted.Configuration,next);
        Check(next.MapBindings.Count==2 && next.MapBindings[1].ConfirmationNote.Contains("逐项"),"重启加载绑定及坐标依据");
        var broken=JsonSerializer.Deserialize<CommunicationConfiguration>(JsonSerializer.Serialize(persisted.Configuration))!;
        broken.MapBindings!.Add(broken.MapBindings[1]);
        try{CommunicationSettingsService.Validate(broken,root);throw new Exception("重复绑定未拒绝");}catch(ArgumentException){checks++;}
        broken.MapBindings.RemoveAt(2);broken.MapBindings[1].RobotMapMd5="bad";
        try{CommunicationSettingsService.Validate(broken,root);throw new Exception("坏MD5未拒绝");}catch(ArgumentException){checks++;}
        var original=Await(service.GetAsync(user,default));
        var legacyClient=JsonSerializer.Deserialize<CommunicationConfiguration>(JsonSerializer.Serialize(original.Saved))!;legacyClient.MapBindings=null;
        var legacySave=Await(service.SaveAsync(user,new(original.Revision,legacyClient),default));
        Check(legacySave.Saved.MapBindings!.Count==2,"旧客户端保存不丢失新绑定字段");
        vm.OnNavigatedTo(null!);Pump(()=>!vm.IsBusy);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
        var view=new ConnectionSettingsView{DataContext=vm};((System.Windows.Controls.TabItem)view.FindName("MapBindingTab")).IsSelected=true;
        Render(view,app,Path.Combine(root,"binding-light.png"));
        var remove=Await(service.GetAsync(user,default));remove.Saved.MapBindings=[];
        var cleared=Await(service.SaveAsync(user,new(remove.Revision,remove.Saved),default));
        var restarted=new AgvOptions{MapBindings=options.MapBindings};CommunicationSettingsService.ApplyAgv(cleared.Saved,restarted);
        Check(restarted.MapBindings.Count==0,"显式移除全部绑定重启后不会回退原配置");
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"history.db")});Await(new DatabaseInitializer(db).InitializeAsync(default));
        var history=new RobokitInspectionHistory(db);
        var snapshot=new RobokitInspectionSnapshot("AGV-01",DateTimeOffset.Now,true,true,"只读成功","3.4.8","1","robot-demo",new string('b',32),1,"MAP","V1",null,null,[],[],[],["只读，不是放行证据"]);
        Await(history.AppendAsync(user.WindowsIdentity,snapshot,default));Await(history.AppendAsync(user.WindowsIdentity,snapshot with{VehicleId="AGV-02",Available=false,Stable=false,Reason="模拟断连"},default));
        try{Await(history.ReadAsync(new(MaximumCount:201),default));throw new Exception("超量读取未阻止");}catch(ArgumentException){checks++;}
        var records=Await(new RobokitInspectionHistory(db).ReadAsync(new("AGV-02"),default));
        Check(records.Count==1 && !records[0].Available && records[0].Reason.Contains("断连") && records[0].Operator==user.WindowsIdentity,"历史持久化、失败原因和按车筛选");
        proxy.Records=records;var monitoring=new Monitor();var agv=new AgvManagementViewModel(monitoring,client);agv.OnNavigatedTo(null!);Await(agv.RefreshInspectionHistoryAsync());
        Check(agv.InspectionHistory.Count==1 && agv.HistoryMessage.Contains("不代表"),"历史显示明确不是当前放行状态");
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>agv);var agvView=new AgvManagementView{DataContext=agv};((System.Windows.Controls.Expander)agvView.FindName("RobokitInspectionPanel")).IsExpanded=true;((System.Windows.Controls.TabItem)agvView.FindName("InspectionHistoryTab")).IsSelected=true;
        Render(agvView,app,Path.Combine(root,"history-light.png"));
        var delayed=new TaskCompletionSource<IReadOnlyList<RobokitInspectionRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.HistoryTask=delayed.Task;var pending=agv.RefreshInspectionHistoryAsync();
        agv.OnNavigatedFrom(null!);agv.OnNavigatedTo(null!);delayed.SetResult(records);Await(pending);
        Check(agv.InspectionHistory.Count==0,"旧异步历史结果不覆盖重新进入页面的选择");
        agv.OnNavigatedFrom(null!);Check(agv.InspectionHistory.Count==0,"离开页面清除历史选择证据");vm.OnNavigatedFrom(null!);
    }
    static void Await(Task t){Pump(()=>t.IsCompleted);t.GetAwaiter().GetResult();}
    static void Render(FrameworkElement view,System.Windows.Application app,string path){((System.Windows.Controls.Control)view).Background=(Brush)app.FindResource("AppBackgroundBrush");view.Width=1500;view.Height=880;view.Measure(new(1500,880));view.Arrange(new(0,0,1500,880));view.UpdateLayout();var bitmap=new RenderTargetBitmap(1500,880,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);png.Save(stream);Check(view.ActualWidth==1500,"XAML渲染 "+Path.GetFileName(path));}
    public class Proxy:DispatchProxy
    {
        public CommunicationSettingsService Service=null!;public TrafficUserContext User=null!;public IReadOnlyList<RobokitInspectionRecord> Records=[];public Task<IReadOnlyList<RobokitInspectionRecord>>? HistoryTask;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
        {"GetCommunicationSettingsAsync"=>Service.GetAsync(User,(CancellationToken)args![0]!),"SaveCommunicationSettingsAsync"=>Service.SaveAsync(User,(SaveCommunicationSettingsRequest)args![0]!, (CancellationToken)args[1]!),"GetRobokitInspectionHistoryAsync"=>HistoryTask ?? Task.FromResult(Records),_=>throw new NotSupportedException()};
    }
    sealed class Tasks:IActiveTaskLookup{public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ActiveDispatchTask>>([]);}
    sealed class Monitor:IDeviceMonitoringClient{public Task<DeviceMonitoringSnapshot> GetDeviceMonitoringAsync(CancellationToken ct)=>Task.FromResult(new DeviceMonitoringSnapshot(DateTimeOffset.Now,[new MonitoredDevice("AGV-02","模拟二号","AGV","关闭","未启用","未启用","—","模拟设备",[])],[]));}
}
