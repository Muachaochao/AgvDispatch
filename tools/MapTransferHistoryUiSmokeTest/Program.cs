using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;
internal static class Program
{
 static int checks;static readonly List<object> results=[];
 static readonly string evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-map-transfer-history-ui");
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
 [STAThread] static int Main(){SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));try{Run();File.WriteAllText(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(new{Passed=checks,Failed=0,Results=results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+checks);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
 static void Await(Task task){if(!task.IsCompleted){var frame=new DispatcherFrame();var end=DateTime.UtcNow.AddSeconds(10);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};timer.Tick+=(_,_)=>{if(task.IsCompleted||DateTime.UtcNow>end)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();}task.GetAwaiter().GetResult();}
 static void Run()
 {
  Directory.CreateDirectory(evidence);var app=new AgvDispatch.Wpf.App();app.InitializeComponent();
  var client=DispatchProxy.Create<ITrafficControlClient,Proxy>();var proxy=(Proxy)(object)client;var monitoring=new Monitoring();var vm=new AgvManagementViewModel(monitoring,client);vm.OnNavigatedTo(null!);
  Await(vm.LoadTransfersAsync());Check(vm.MapTransfers.Count==25&&vm.TransferMessage.Contains("26"),"所选车辆列表25项及总数展示");
  Await(vm.LoadTransfersAsync(2));Check(vm.MapTransfers.Count==1&&vm.MapTransfers[0].OperationId=="OP-25","列表第二页读取");
  vm.SelectedTransfer=vm.MapTransfers[0];Await(vm.LoadTransferDetailAsync());Check(vm.MapTransferEvents.Count==25&&vm.TransferDetail.Contains("关闭、补发、放行入口均未开放"),"历史及只读处置条件展示");
  Await(vm.LoadTransferDetailAsync(false,2));Check(vm.MapTransferEvents.Count==2&&vm.TransferDetail.Contains("历史第 2 页"),"历史独立分页27项");
  Await(vm.LoadTransferDetailAsync(true));Check(proxy.ObserveCalls==1&&vm.TransferDetail.Contains("Uncertain"),"手动只读复核后仍不确定，无关闭动作");
  vm.TransferState="Uncertain";Check(vm.MapTransfers.Count==0&&vm.MapTransferEvents.Count==0&&vm.SelectedTransfer is null,"筛选变化清空旧记录及证据");
  vm.TransferFrom="not-a-date";var calls=proxy.ListCalls;Await(vm.LoadTransfersAsync());Check(proxy.ListCalls==calls&&vm.TransferMessage.Contains("时间格式无效"),"无效时间在界面明确拒绝，未请求后台");vm.TransferFrom="";
  Await(vm.LoadTransfersAsync());vm.SelectedTransfer=vm.MapTransfers[0];Await(vm.LoadTransferDetailAsync());
  ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var view=new AgvManagementView{DataContext=vm};((Expander)view.FindName("MapTransferPanel")).IsExpanded=true;
  Render(view,app,"history-list-light",1440,900);
  var tabs=Descendants(view).OfType<TabItem>().First(x=>Equals(x.Header,"只读历史与处置条件"));tabs.IsSelected=true;Render(view,app,"history-detail-light",1440,900);
  new AgvDispatch.Wpf.Services.ThemeService().Toggle();
  var darkView=new AgvManagementView{DataContext=vm};((Expander)darkView.FindName("MapTransferPanel")).IsExpanded=true;
  darkView.Measure(new(1100,700));darkView.Arrange(new(0,0,1100,700));darkView.UpdateLayout();
  Descendants(darkView).OfType<TabItem>().First(x=>Equals(x.Header,"只读历史与处置条件")).IsSelected=true;
  Check(((SolidColorBrush)darkView.Foreground).Color==((SolidColorBrush)app.FindResource("TextPrimaryBrush")).Color,"暗色新视图采用当前主题文字颜色");
  Render(darkView,app,"history-detail-dark-small",1100,700);
  vm.SelectedDevice=vm.Devices[1];Check(vm.MapTransfers.Count==0&&vm.MapTransferEvents.Count==0,"换车清除原车辆列表及历史");
  proxy.HoldList=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=vm.LoadTransfersAsync();vm.SelectedDevice=vm.Devices[0];proxy.HoldList.SetResult(new([proxy.Item("B",0)],1,1,25));Await(pending);Check(vm.MapTransfers.Count==0,"忽略取消的旧列表返回不覆盖新车辆");proxy.HoldList=null;
  Await(vm.LoadTransfersAsync());proxy.BadVehicle=true;Await(vm.LoadTransfersAsync());Check(vm.MapTransfers.Count==0&&vm.TransferMessage.Contains("归属"),"错误车辆列表整批拒绝");proxy.BadVehicle=false;
  Await(vm.LoadTransfersAsync());vm.SelectedTransfer=vm.MapTransfers[0];Await(vm.LoadTransferDetailAsync());
  proxy.HoldReview=new(TaskCreationOptions.RunContinuationsAsynchronously);var detail=vm.LoadTransferDetailAsync();vm.SelectedTransfer=null;proxy.HoldReview.SetResult(proxy.Review("A","OP-00"));Await(detail);Check(vm.MapTransferEvents.Count==0&&!vm.TransferDetail.Contains("源SHA256"),"迟到历史/处置计划不覆盖已清除选择");proxy.HoldReview=null;
  proxy.Fail=true;Await(vm.LoadTransfersAsync());Check(vm.MapTransfers.Count==0&&vm.TransferMessage.Contains("离线"),"读取失败清空旧记录");proxy.Fail=false;
  Await(vm.LoadTransfersAsync());vm.SelectedTransfer=vm.MapTransfers[0];Await(vm.LoadTransferDetailAsync());vm.OnNavigatedFrom(null!);Check(vm.MapTransfers.Count==0&&vm.MapTransferEvents.Count==0,"离页清空记录与处置条件");calls=proxy.ListCalls;Await(vm.LoadTransfersAsync());Check(proxy.ListCalls==calls,"离页不发历史查询");
  Check(proxy.OtherCalls==0,"界面仅调用列表/历史/计划及只读观察，无准备/上传/关闭/放行");
 }
 static IEnumerable<DependencyObject> Descendants(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var desc in Descendants(child))yield return desc;}}
 static void Render(FrameworkElement view,System.Windows.Application app,string name,int width,int height)
 {
  view.Width=width;view.Height=height;((Control)view).Background=(Brush)app.FindResource("AppBackgroundBrush");view.Measure(new(width,height));view.Arrange(new(0,0,width,height));view.UpdateLayout();
  var panel=(Expander)view.FindName("MapTransferPanel");Check(panel.ActualHeight>300&&panel.TranslatePoint(new(0,panel.ActualHeight),view).Y<=height,name+"实际XAML面板可见");
  var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(evidence,name+".png"));encoder.Save(stream);
 }
 public class Proxy:DispatchProxy
 {
  public int ListCalls,ObserveCalls,OtherCalls;public bool Fail,BadVehicle;public TaskCompletionSource<MapTransferPage>? HoldList;public TaskCompletionSource<MapTransferReviewPlan>? HoldReview;
  public MapTransferRecord Item(string car,int i)=>new($"OP-{i:D2}","REQ","MAP","V1",car,"sim-map","Uncertain",1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
  public MapTransferReviewPlan Review(string car,string id)=>new(new(id,"REQ","MAP","V1",car,"sim-map",new string('a',64),new string('b',32),"Uncertain",1,true,false,["模拟不确定屏障"],"明确模拟，非本车返回",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow),true,["原操作证据","厂家MD5规则未核定"],["禁止自动補发"]);
  protected override object? Invoke(MethodInfo? method,object?[]? args)
  {
   switch(method?.Name){
    case "ListMapTransfersAsync":{ListCalls++;if(HoldList is not null)return HoldList.Task;if(Fail)return Task.FromException<MapTransferPage>(new IOException("模拟离线"));var q=(MapTransferListQuery)args![0]!;var items=Enumerable.Range(0,26).Skip((q.Page-1)*25).Take(25).Select(i=>Item(BadVehicle?"WRONG":q.VehicleId!,i)).ToArray();return Task.FromResult(new MapTransferPage(items,26,q.Page,25));}
    case "GetMapTransferReviewAsync":{if(HoldReview is not null)return HoldReview.Task;return Task.FromResult(Review("A",((MapTransferQuery)args![0]!).OperationId));}
    case "GetMapTransferHistoryAsync":{var q=(MapTransferHistoryQuery)args![0]!;return Task.FromResult(new MapTransferHistoryPage(q.OperationId,Enumerable.Range(0,27).Skip((q.Page-1)*25).Take(25).Select(i=>new MapTransferEventSnapshot("Simulated","test",$"sample-{i}",DateTimeOffset.UtcNow)).ToArray(),27,q.Page,25));}
    case "ObserveMapTransferAsync":ObserveCalls++;return Task.FromResult(Review("A",((MapTransferQuery)args![0]!).OperationId).Record);
    default:OtherCalls++;throw new NotSupportedException(method?.Name);
   }
  }
 }
 sealed class Monitoring:IDeviceMonitoringClient
 {
  public Task<DeviceMonitoringSnapshot> GetDeviceMonitoringAsync(CancellationToken ct)=>Task.FromResult(new DeviceMonitoringSnapshot(DateTimeOffset.Now,new[]{"A","B"}.Select(x=>new MonitoredDevice(x,"模拟车辆"+x,"AGV","未启用","只读","未知","—","测试数据，非现场",[])).ToArray(),[]));
 }
}
