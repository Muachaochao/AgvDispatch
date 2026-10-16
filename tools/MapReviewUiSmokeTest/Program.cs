using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    static readonly string Evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-map-review-ui");
    static readonly string Positive=Path.GetFullPath("docs/commissioning/evidence/20261015-robokit-map-cross-review/positive-final/review.json");
    static int checks;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    static void Complete(Task task){var deadline=DateTime.UtcNow.AddSeconds(35);while(!task.IsCompleted&&DateTime.UtcNow<deadline)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
    static T Await<T>(Task<T> task){Complete(task);return task.Result;}
    static string Copy(string label)
    {
        var dest=Path.Combine(Evidence,label+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(dest,"inputs"));File.Copy(Positive,Path.Combine(dest,"review.json"));foreach(var file in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Positive)!,"inputs")))File.Copy(file,Path.Combine(dest,"inputs",Path.GetFileName(file)));return Path.Combine(dest,"review.json");
    }
    static void Reject(string label,Action<JsonNode> mutation)
    {
        var path=Copy(label);var node=JsonNode.Parse(File.ReadAllText(path))!;mutation(node);File.WriteAllText(path,node.ToJsonString());
        try{Await(new MapReviewReportReader().ReadAsync(path,default));throw new Exception("未拒绝："+label);}catch(Exception ex)when(ex is InvalidDataException or JsonException or InvalidOperationException or FormatException){Check(true,label);}
    }
    static void Render(ConnectionSettingsViewModel vm,string name,int width=1500,int height=900,string tab="逐站差异")
    {
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var view=new ConnectionSettingsView{DataContext=vm,Width=width,Height=height,Background=(Brush)System.Windows.Application.Current.FindResource("AppBackgroundBrush")};
        ((TabItem)view.FindName("MapBindingTab")).IsSelected=true;((TabItem)view.FindName("MapReviewTab")).IsSelected=true;
        view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        foreach(var item in Descendants(view).OfType<TabItem>())if(item.Header?.ToString()==tab)item.IsSelected=true;
        view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(Evidence,name+".png"));png.Save(stream);
        Check(Descendants(view).OfType<DataGrid>().Where(g=>g.IsVisible).All(g=>g.IsReadOnly),name+" 可见表格只读");
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject node){for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    [STAThread]static int Main()
    {
        Directory.CreateDirectory(Evidence);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            foreach(var u in new[]{"HandyControl;component/Themes/SkinDefault.xaml","HandyControl;component/Themes/Theme.xaml","AgvDispatch.Wpf;component/Themes/Light.xaml","AgvDispatch.Wpf;component/Themes/HandyPalette.xaml","AgvDispatch.Wpf;component/Styles/Typography.xaml","AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml","AgvDispatch.Wpf;component/Styles/Controls.xaml","AgvDispatch.Wpf;component/Styles/HandyControls.xaml"})app.Resources.MergedDictionaries.Add(new(){Source=new Uri("pack://application:,,,/"+u)});
            var reader=new MapReviewReportReader();var report=Await(reader.ReadAsync(Positive,default));
            Check(report.Mode=="synthetic"&&report.Stations.Count==4&&report.Routes.Count==4&&report.Behaviors.Count==7,"正例四站四段七类行为读取");
            Check(report.RobotMap=="SIM-MAP"&&report.VehicleAlias=="TEST-SYNTHETIC"&&report.LocalMapId=="未提供","身份来自哈希核对输入，不猜本地版本");
            Check(report.Stations.All(s=>s.XyDeltaM==0&&s.LocalDeltaM==0)&&report.Sha256.Length==64,"数值和报告SHA保留");
            var negative=Path.GetFullPath("docs/commissioning/evidence/20261015-robokit-map-cross-review/negative-device_xy/review.json");var bad=Await(reader.ReadAsync(negative,default));Check(!bad.ComparisonPassed&&bad.Stations.Any(s=>s.Status=="FAIL")&&bad.Errors.Count>0,"合法差异报告可导入，不当损坏文件拒绝");
            foreach(var key in new[]{"realDeviceValidated","coordinateAlignmentVerified","behaviorVerified","canEnableCommands","canUploadMap"})Reject("批准字段"+key,n=>n[key]=true);
            Reject("未知来源模式",n=>n["mode"]="device_approved");Reject("来源矛盾",n=>n["originDeclaration"]="real_device_read_only_capture");
            Reject("输入角色路径穿越",n=>n["inputs"]![0]!["role"]="../outside");Reject("缺少输入清单",n=>n["inputs"]!.AsArray().RemoveAt(0));
            Reject("重复输入角色",n=>n["inputs"]![1]!["role"]=n["inputs"]![0]!["role"]!.GetValue<string>());
            Reject("长度超预算",n=>n["inputs"]![0]!["bytes"]=int.MaxValue);Reject("输入哈希不一致",n=>n["inputs"]![0]!["sha256"]=new string('a',64));
            Reject("重复站点",n=>n["stations"]!.AsArray().Add(n["stations"]![0]!.DeepClone()));
            Reject("负误差",n=>n["stations"]![0]!["xyDeltaM"]=-1);Reject("无穷数值",n=>n["stations"]![0]!["xyDeltaM"]=JsonValue.Create("NaN"));
            Reject("未知站点结果",n=>n["stations"]![0]!["status"]="APPROVED");Reject("行为自批准",n=>n["behaviorEvidence"]![0]!["verified"]=true);
            Reject("总结果与明细矛盾",n=>n["errors"]!.AsArray().Add("test failure"));
            Reject("站点数量超限",n=>{var rows=n["stations"]!.AsArray();var one=rows[0]!.DeepClone();while(rows.Count<=10000)rows.Add(one.DeepClone());});
            var missing=Copy("missing");File.Delete(Path.Combine(Path.GetDirectoryName(missing)!,"inputs","capture.json"));try{Await(reader.ReadAsync(missing,default));throw new Exception("未拒缺文件");}catch(FileNotFoundException){Check(true,"缺伴随输入拒绝");}
            var duplicate=Copy("duplicate-key");File.WriteAllText(duplicate,File.ReadAllText(duplicate).Replace("\"mode\": \"synthetic\",","\"mode\": \"synthetic\",\"mode\": \"synthetic\","));try{Await(reader.ReadAsync(duplicate,default));throw new Exception("未拒重复键");}catch(InvalidDataException){Check(true,"重复JSON键拒绝");}
            var huge=Copy("oversize");using(var stream=File.OpenWrite(huge))stream.SetLength((8<<20)+1);try{Await(reader.ReadAsync(huge,default));throw new Exception("未拒超文件");}catch(InvalidDataException){Check(true,"真实超8MiB报告拒绝");}
            var cancelled=new CancellationToken(true);try{Await(reader.ReadAsync(Positive,cancelled));throw new Exception("未取消");}catch(OperationCanceledException){Check(true,"导入取消令牌有效");}
            var proxy=DispatchProxy.Create<ITrafficControlClient,NoCalls>();var vm=new ConnectionSettingsViewModel(proxy);
            var binding=new AgvMapBindingSetting{VehicleId="TEST-SYNTHETIC",LocalMapId="MAP",LocalMapVersion="V1",RobotMap="SIM-MAP",RobotMapMd5=report.RobotMapMd5,CoordinatesVerified=false};vm.MapBindings.Add(binding);vm.SelectedMapBinding=binding;
            var fingerprint=JsonSerializer.Serialize(binding);Complete(vm.ImportMapReviewAsync(Positive));
            Check(vm.MapReviewStations.Count==4&&vm.MapReviewMessage.Contains("合成模拟")&&vm.MapReviewContext.Contains("仅文本"),"真实ViewModel只读导入与上下文展示");
            Check(!vm.CanManage&&!vm.SaveCommand.CanExecute()&&JsonSerializer.Serialize(binding)==fingerprint,"只读用户可导入但不能保存，绑定字段未改");
            Render(vm,"positive-light");Render(vm,"routes-light",tab:"四段运输");Render(vm,"small-light",1100,700);
            Check(report.Routes[0].RoleText.Contains("取货")&&report.Behaviors[0].StatusText=="缺少资料","运输与行为采用中文说明");
            Complete(vm.ImportMapReviewAsync(negative));vm.ShowOnlyMapDifferences=true;Check(vm.MapReviewStations.Count>0&&vm.MapReviewStations.All(s=>s.Status!="PASS"),"只显示差异和待核定过滤");Render(vm,"differences-light");vm.ShowOnlyMapDifferences=false;
            var invalid=Path.Combine(Evidence,"invalid.json");File.WriteAllText(invalid,"{}");Complete(vm.ImportMapReviewAsync(invalid));Check(vm.MapReview is null&&vm.MapReviewStations.Count==0&&vm.MapReviewMessage.Contains("失败"),"坏文件不残留旧报告");
            Complete(vm.ImportMapReviewAsync(Positive));binding.RobotMap="OTHER";Check(vm.MapReview is null,"编辑绑定字段清空旧报告");binding.RobotMap="SIM-MAP";
            Complete(vm.ImportMapReviewAsync(Positive));vm.SelectedMapBinding=new(){VehicleId="OTHER"};Check(vm.MapReview is null,"切绑定清空报告");vm.SelectedMapBinding=binding;
            Complete(vm.ImportMapReviewAsync(Positive));vm.SelectedAgv=new(){VehicleId="OTHER"};Check(vm.MapReview is null,"切车清空报告");
            Complete(vm.ImportMapReviewAsync(Positive));vm.SelectedAgv.VehicleId="EDITED";Check(vm.MapReview is null,"编辑车辆身份清空报告");
            Complete(vm.ImportMapReviewAsync(Positive));vm.OnNavigatedFrom(null!);Check(vm.MapReview is null,"离页清空报告");
            var delayed=new DelayedReader();vm.MapReviewReader=delayed;var old=vm.ImportMapReviewAsync("delayed");vm.SelectedMapBinding=new(){VehicleId="NEW"};delayed.Completion.SetResult(report);Complete(old);Check(vm.MapReview is null&&!vm.IsImportingMapReview,"忽略取消的旧读取不会在换绑定后复活");
            delayed=new();vm.MapReviewReader=delayed;old=vm.ImportMapReviewAsync("old");vm.MapReviewReader=reader;Complete(vm.ImportMapReviewAsync(negative));delayed.Completion.SetResult(report);Complete(old);Check(vm.MapReview is {ComparisonPassed:false},"重新导入后旧读取不覆盖新差异");
            vm.ClearMapReviewCommand.Execute();Check(vm.MapReview is null,"主动清空有效");Check(((NoCalls)proxy).Calls==0,"导入过滤渲染切换均零后台及设备调用");
            delayed=new();vm.MapReviewReader=delayed;old=vm.ImportMapReviewAsync("cancel");vm.ClearMapReviewCommand.Execute();delayed.Completion.SetResult(report);Complete(old);Check(vm.MapReview is null&&!vm.IsImportingMapReview,"主动取消后迟到结果不复活");
            vm.MapReviewReader=reader;Complete(vm.ImportMapReviewAsync(Positive));
            var light=app.Resources.MergedDictionaries.First(d=>d.Source?.OriginalString.Contains("/Themes/Light.xaml")==true);light.Source=new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/Dark.xaml");
            Render(vm,"behaviors-dark",tab:"厂家行为");Render(vm,"small-dark",1100,700);
            File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=checks,Failed=0}));Console.WriteLine("Passed "+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());return 1;}
    }
    public class NoCalls:DispatchProxy{public int Calls;protected override object? Invoke(MethodInfo? method,object?[]? args){Calls++;throw new InvalidOperationException("禁止后台调用");}}
    sealed class DelayedReader:IMapReviewReportReader{public TaskCompletionSource<MapReviewReport> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);public Task<MapReviewReport> ReadAsync(string path,CancellationToken token)=>Completion.Task;}
}
