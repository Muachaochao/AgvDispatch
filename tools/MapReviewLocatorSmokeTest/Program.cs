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
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static partial class Program
{
    static readonly string Evidence=Path.GetFullPath("docs/commissioning/evidence/20261016-map-review-segments");
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
        if(name=="row-binding-light")
        {
            var grid=Descendants(view).OfType<DataGrid>().First(g=>g.Items.OfType<MapReviewStation>().Any());grid.SelectedItem=grid.Items.OfType<MapReviewStation>().First();
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            Check(vm.ReviewHasMarker&&vm.ReviewLocatorOpen&&ReferenceEquals(vm.SelectedReviewStation,grid.SelectedItem),"实际DataGrid选行绑定触发快照定位");
        }
        if(name=="route-row-binding")
        {
            var grid=Descendants(view).OfType<DataGrid>().First(g=>g.Items.OfType<MapReviewRoute>().Any());grid.SelectedItem=grid.Items.OfType<MapReviewRoute>().Skip(1).First();
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            Check(vm.ReviewRouteHighlights.Count==1&&vm.ReviewLocatorOpen&&ReferenceEquals(vm.SelectedReviewRoute,grid.SelectedItem),"正式运输DataGrid绑定高亮路线");
        }
        if(name=="locator-dark-small")
        {
            var canvas=Descendants(view).OfType<Canvas>().First(c=>c.Width==900&&c.Height==520&&c.ClipToBounds);
            Check(canvas.TransformToAncestor(view).TransformBounds(new Rect(0,0,900,520)).Height>180,"1100x700定位页折叠概览后地图高度充足");
        }
        Window? renderHost=null;
        if(name=="routes-dark-small")
        {
            renderHost=new Window{Content=view,Width=width+20,Height=height+45,ShowActivated=false,ShowInTaskbar=false,Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual};
            renderHost.Show();view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
        }
        CheckSegmentUi(vm,view,name);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(Evidence,name+".png"));png.Save(stream);
        if(renderHost is not null){renderHost.Content=null;renderHost.Close();}
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
            CheckLocator(vm,report);
            CheckRoutes(vm);
            CheckSegments(vm);
            Check(!vm.CanManage&&!vm.SaveCommand.CanExecute()&&JsonSerializer.Serialize(binding)==fingerprint,"只读用户可导入但不能保存，绑定字段未改");
            Render(vm,"positive-light");Render(vm,"routes-light",tab:"四段运输");Render(vm,"small-light",1100,700);
            Check(report.Routes[0].RoleText.Contains("取货")&&report.Behaviors[0].StatusText=="缺少资料","运输与行为采用中文说明");
            Complete(vm.ImportMapReviewAsync(negative));vm.ShowOnlyMapDifferences=true;Check(vm.MapReviewStations.Count>0&&vm.MapReviewStations.All(s=>s.Status!="PASS"),"只显示差异和待核定过滤");Render(vm,"differences-light");vm.ShowOnlyMapDifferences=false;
            var invalid=Path.Combine(Evidence,"invalid.json");File.WriteAllText(invalid,"{}");Complete(vm.ImportMapReviewAsync(invalid));Check(vm.MapReview is null&&vm.MapReviewStations.Count==0&&vm.MapReviewMessage.Contains("失败"),"坏文件不残留旧报告");
            Complete(vm.ImportMapReviewAsync(Positive));binding.RobotMap="OTHER";Check(vm.MapReview is null,"编辑绑定字段清空旧报告");binding.RobotMap="SIM-MAP";
            Complete(vm.ImportMapReviewAsync(Positive));vm.SelectedMapBinding=new(){VehicleId="OTHER"};Check(vm.MapReview is null,"切绑定清空报告");vm.SelectedMapBinding=binding;
            Complete(vm.ImportMapReviewAsync(Positive));vm.SelectedAgv=new(){VehicleId="OTHER"};Check(vm.MapReview is null&&!vm.ReviewHasMarker&&vm.ReviewMapDots.Count==0&&vm.ReviewSearchResults.Count==0,"切车清空报告和地图定位");
            Complete(vm.ImportMapReviewAsync(Positive));vm.SelectedAgv.VehicleId="EDITED";Check(vm.MapReview is null,"编辑车辆身份清空报告");
            Complete(vm.ImportMapReviewAsync(Positive));vm.OnNavigatedFrom(null!);Check(vm.MapReview is null,"离页清空报告");
            var delayed=new DelayedReader();vm.MapReviewReader=delayed;var old=vm.ImportMapReviewAsync("delayed");vm.SelectedMapBinding=new(){VehicleId="NEW"};delayed.Completion.SetResult(report);Complete(old);Check(vm.MapReview is null&&!vm.IsImportingMapReview,"忽略取消的旧读取不会在换绑定后复活");
            delayed=new();vm.MapReviewReader=delayed;old=vm.ImportMapReviewAsync("old");vm.MapReviewReader=reader;Complete(vm.ImportMapReviewAsync(negative));delayed.Completion.SetResult(report);Complete(old);Check(vm.MapReview is {ComparisonPassed:false},"重新导入后旧读取不覆盖新差异");
            vm.ClearMapReviewCommand.Execute();Check(vm.MapReview is null,"主动清空有效");Check(((NoCalls)proxy).Calls==0,"导入过滤渲染切换均零后台及设备调用");
            delayed=new();vm.MapReviewReader=delayed;old=vm.ImportMapReviewAsync("cancel");vm.ClearMapReviewCommand.Execute();delayed.Completion.SetResult(report);Complete(old);Check(vm.MapReview is null&&!vm.IsImportingMapReview,"主动取消后迟到结果不复活");
            vm.MapReviewReader=reader;Complete(vm.ImportMapReviewAsync(Positive));
            var light=app.Resources.MergedDictionaries.First(d=>d.Source?.OriginalString.Contains("/Themes/Light.xaml")==true);light.Source=new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/Dark.xaml");
            vm.ReviewSearchQuery="LM0101";vm.ReviewSearchMode="厂家编号";vm.SearchReviewMap();Render(vm,"locator-dark-small",1100,700,tab:"地图定位与搜索");
            vm.ReviewRouteRole="全部四段";vm.HighlightReviewRoutes();vm.SelectReviewSegment(vm.ReviewRouteSegments[2]);Render(vm,"routes-dark-small",1100,700,tab:"地图定位与搜索");
            Render(vm,"behaviors-dark",tab:"厂家行为");Render(vm,"small-dark",1100,700);
            File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=checks,Failed=0}));Console.WriteLine("Passed "+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());return 1;}
    }
    static void CheckLocator(ConnectionSettingsViewModel vm,MapReviewReport report)
    {
        report=vm.MapReview!;
        Check(report.Snapshot is {Nodes.Count:4,Roads.Count:4}&&report.Snapshot.Sha256.Length==64,"已核对原字节生成四点四路快照");
        var before=JsonSerializer.Serialize(vm.SelectedMapBinding);
        var site=report.Stations.Single(s=>s.LogicalId=="N_PICK");vm.SelectedReviewStation=site;
        Check(vm.ReviewHasMarker&&vm.ReviewLocatorOpen&&vm.ReviewLocationMessage.Contains("N_PICK"),"选择差异行跳转只读快照定位");
        Check(Math.Abs((vm.ReviewMarkerX+12-450)*vm.ReviewZoom+450+vm.ReviewPanX-450)<1e-6&&Math.Abs((vm.ReviewMarkerY+12-260)*vm.ReviewZoom+260+vm.ReviewPanY-260)<1e-6,"定位标记投影居中且Y轴方向正确");
        Check(vm.ReviewLocationMessage.Contains("未提供")&&vm.ReviewLocationMessage.Contains("不联动"),"缺当前地图版本仅看报告快照，不猜运行图");
        Render(vm,"row-location-light",tab:"地图定位与搜索");Render(vm,"row-binding-light");
        vm.ReviewSearchQuery="LM0202";vm.ReviewSearchMode="厂家编号";vm.SearchReviewMap();
        Check(vm.ReviewSearchResults.Count==1&&vm.ReviewHasMarker&&vm.ReviewLocationMessage.Contains("N_DUMP"),"厂家精确编号定位业务点");
        vm.ReviewSearchMode="业务编号";vm.ReviewSearchQuery="n_wait";vm.SearchReviewMap();
        Check(vm.ReviewHasMarker&&vm.ReviewLocationMessage.Contains("N_WAIT"),"业务编号大小写搜索可用");
        var stale=vm.SelectedReviewHit!;vm.ReviewSearchQuery="LM";vm.ReviewSearchMode="自动";vm.SearchReviewMap();
        Check(vm.ReviewSearchResults.Count==4&&!vm.ReviewHasMarker,"部分匹配列出候选不擅选首项");
        vm.SelectedReviewHit=vm.ReviewSearchResults[2];Check(vm.ReviewHasMarker,"点击候选可定位");
        vm.SelectedReviewHit=stale;Check(!vm.ReviewHasMarker,"旧搜索候选不能复用");
        vm.LocateReviewStation(site with{LogicalId="FORGED"});Check(!vm.ReviewHasMarker,"外来或旧报告行拒绝定位");
        vm.ReviewSearchQuery="NOT-FOUND";vm.SearchReviewMap();Check(vm.ReviewSearchResults.Count==0&&!vm.ReviewHasMarker,"未命中清空旧标记");
        vm.ReviewSearchQuery="";vm.SearchReviewMap();Check(!vm.ReviewHasMarker,"空搜索无定位");
        vm.ReviewSearchQuery=new string('X',257);vm.SearchReviewMap();Check(vm.ReviewSearchResults.Count==0,"过长查询拒绝");
        vm.ReviewSearchQuery="LM0001";vm.SearchReviewMap();vm.AdjustReviewZoom(1000);Check(vm.ReviewZoom==8,"放大有界");
        vm.AdjustReviewZoom(.00001);Check(vm.ReviewZoom==.5,"缩小有界");vm.FitReviewMapCommand.Execute();Check(vm.ReviewZoom==1&&vm.ReviewPanX==0&&vm.ReviewPanY==0,"恢复全图");
        Check(JsonSerializer.Serialize(vm.SelectedMapBinding)==before&&!vm.SelectedMapBinding!.CoordinatesVerified,"定位搜索不改变绑定或坐标确认");
        var root=JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Positive)!,"inputs/local_map.json")))!;
        var text=root.ToJsonString();using(var doc=JsonDocument.Parse(text))
        {
            var copied=MapReviewSnapshot.Read(doc.RootElement,"test-sha",default);Check(copied.Nodes[1].X==5&&copied.Nodes[1].Y==0,"快照使用本地米制坐标，不混车端平移值");
        }
        var modified=root.DeepClone();modified["unit"]="mm";
        try{using var doc=JsonDocument.Parse(modified.ToJsonString());MapReviewSnapshot.Read(doc.RootElement,"test",default);throw new Exception("mm未拒绝");}catch(InvalidDataException){Check(true,"非米单位快照不猜换算");}
        modified=root.DeepClone();modified["nodes"]![1]!["id"]=modified["nodes"]![0]!["id"]!.GetValue<string>();
        try{using var doc=JsonDocument.Parse(modified.ToJsonString());MapReviewSnapshot.Read(doc.RootElement,"test",default);throw new Exception("重复节点未拒绝");}catch(InvalidDataException){Check(true,"重复业务点拒绝快照定位");}
        modified=root.DeepClone();modified["edges"]![0]!["geometryKind"]="CubicBezier";modified["edges"]![0]!["controlPoints"]=JsonNode.Parse("[{\"x\":1,\"y\":2},{\"x\":4,\"y\":2}]");
        using(var doc=JsonDocument.Parse(modified.ToJsonString())){var snapshot=MapReviewSnapshot.Read(doc.RootElement,"test",default);Check(snapshot.Roads[0].Count==33&&snapshot.Roads[0].Any(p=>p.Y>0),"Bezier使用共享几何采样，不以直线代替");}
        modified=root.DeepClone();modified["edges"]![0]!["geometryKind"]="CircularArc";modified["edges"]![0]!["controlPoints"]=JsonNode.Parse("[{\"x\":2.5,\"y\":2.5}]");
        using(var doc=JsonDocument.Parse(modified.ToJsonString())){var snapshot=MapReviewSnapshot.Read(doc.RootElement,"test",default);Check(snapshot.Roads[0].Count==33&&snapshot.Roads[0].Any(p=>Math.Abs(p.Y)>1),"圆弧使用真实过弧点几何");}
        var workspace=new JsonObject{["draft"]=new JsonObject{["definition"]=root.DeepClone()}};
        using(var doc=JsonDocument.Parse(workspace.ToJsonString())){Check(MapReviewSnapshot.Read(doc.RootElement,"test",default).Nodes.Count==4,"agvmap草稿快照读取");}
        var proxy=DispatchProxy.Create<ITrafficControlClient,NoCalls>();var other=new ConnectionSettingsViewModel(proxy);
        var largeNodes=Enumerable.Range(0,3000).Select(i=>new MapNode("NODE"+i,i%100,i/100,MapNodeType.Waypoint,false,[],"V"+i)).ToArray();
        var large=report with{Snapshot=new("large",new string('f',64),largeNodes,[],0,0)};
        other.MapReviewReader=new ImmediateReader(large);Complete(other.ImportMapReviewAsync("test-only"));
        Check(other.ReviewMapDots.Count==2000,"大图显示节点有界且不丢可搜索数据");
        other.ReviewSearchQuery="NODE";other.SearchReviewMap();Check(other.ReviewSearchResults.Count==200&&!other.ReviewHasMarker&&other.ReviewLocationMessage.Contains("3000"),"大图候选显示200且明确总数");
        other.ReviewSearchQuery="V2999";other.SearchReviewMap();Check(other.ReviewHasMarker&&other.ReviewMapDots.Any(p=>p.Id=="NODE2999")&&other.ReviewMapDots.Count==2001,"未绘制节点仍能精确搜索并保留选中标记");
        var collision=report with{Snapshot=new("collision",new string('e',64),[new("A",0,0,MapNodeType.Waypoint,false,[],"X"),new("X",1,0,MapNodeType.Waypoint,false,[],"B")],[],0,0)};
        other.MapReviewReader=new ImmediateReader(collision);Complete(other.ImportMapReviewAsync("test-only"));other.ReviewSearchQuery="X";other.SearchReviewMap();
        Check(other.ReviewSearchResults.Count==2&&!other.ReviewHasMarker,"业务和厂家命名空间冲突不自动猜候选");
        other.SelectedReviewHit=other.ReviewSearchResults[0];Check(other.ReviewHasMarker&&other.ReviewLocationMessage.Contains("业务 A"),"歧义候选显式选行定位");
        var mismatch=report.Stations.Select(r=>r.LogicalId=="N_PICK"?r with{SiteId="WRONG"}:r).ToArray();other.MapReviewReader=new ImmediateReader(report with{Stations=mismatch});Complete(other.ImportMapReviewAsync("test-only"));other.SelectedReviewStation=other.MapReview!.Stations.Single(r=>r.LogicalId=="N_PICK");Check(!other.ReviewHasMarker,"报告业务ID与厂家ID不对应拒绝定位");
        other.MapReviewReader=new ImmediateReader(report with{Snapshot=null,SnapshotIssue="test unavailable"});Complete(other.ImportMapReviewAsync("test-only"));other.SearchReviewMap();Check(!other.ReviewHasMarker&&other.ReviewMapDots.Count==0,"无可用快照不沿用旧图");
        Check(((NoCalls)proxy).Calls==0,"大图与歧义定位无后台查询");
        modified=root.DeepClone();modified["edges"]![0]!["geometryKind"]="UNKNOWN";
        using(var doc=JsonDocument.Parse(modified.ToJsonString())){var snapshot=MapReviewSnapshot.Read(doc.RootElement,"test",default);Check(snapshot.SkippedRoads==1&&snapshot.Roads.Count==3,"未知道路不画伪造直线");}
    }
    static void CheckRoutes(ConnectionSettingsViewModel vm)
    {
        Complete(vm.ImportMapReviewAsync(Positive));var original=vm.MapReview!;var snapshot=original.Snapshot!;
        vm.ReviewSearchMode="路段编号";vm.ReviewSearchQuery="e_home_pick";vm.SearchReviewMap();
        Check(vm.ReviewSearchResults.Count==1&&vm.SelectedReviewHit!.IsRoad&&vm.ReviewRouteHighlights.Count==1&&!vm.ReviewHasMarker,"路段精确编号大小写搜索高亮");
        var stale=vm.SelectedReviewHit;vm.ReviewSearchQuery="E_";vm.SearchReviewMap();Check(vm.ReviewSearchResults.Count==4&&vm.ReviewRouteHighlights.Count==0,"路段部分匹配列候选清旧高亮");
        vm.SelectedReviewHit=vm.ReviewSearchResults[1];Check(vm.ReviewRouteHighlights.Count==1,"选择路段候选高亮");vm.SelectedReviewHit=stale;Check(vm.ReviewRouteHighlights.Count==0,"过期候选清空高亮");
        vm.ReviewRouteRole="全部四段";vm.HighlightReviewRoutes();Check(vm.ReviewRouteHighlights.Count==4&&vm.ReviewRouteHighlights.Select(h=>h.Color).Distinct().Count()==4,"四段四色路线高亮");Render(vm,"four-routes-light",tab:"地图定位与搜索");
        for(int i=1;i<=4;i++){vm.ReviewRouteRole=vm.ReviewRouteRoles[i];vm.HighlightReviewRoutes();Check(vm.ReviewRouteHighlights.Count==1,"单角色路线高亮"+i);}
        vm.SelectedReviewRoute=original.Routes[0];Check(vm.ReviewLocatorOpen&&vm.ReviewRouteHighlights.Count==1,"运输行选择触发角色高亮");Render(vm,"route-row-binding",tab:"四段运输");vm.LocateReviewRoute(original.Routes[0] with{});Check(vm.ReviewRouteHighlights.Count==0,"外来同值运输行拒绝");
        var proxy=DispatchProxy.Create<ITrafficControlClient,NoCalls>();var other=new ConnectionSettingsViewModel(proxy);
        void Load(MapReviewReport r){other.MapReviewReader=new ImmediateReader(r);Complete(other.ImportMapReviewAsync("synthetic-test"));other.HighlightReviewRoutes();}
        Load(original with{Routes=original.Routes.Select((r,i)=>i==0?r with{VendorFrom="WRONG"}:r).ToArray()});Check(other.ReviewRouteHighlights.Count==0,"厂家端点矛盾拒绝整批");
        Load(original with{Routes=original.Routes.Skip(1).ToArray()});Check(other.ReviewRouteHighlights.Count==0,"缺角色拒绝部分成功");
        Load(original with{Routes=original.Routes.Select((r,i)=>i==0?r with{Step=2}:r).ToArray()});Check(other.ReviewRouteHighlights.Count==0,"运输序号断档拒绝");
        Load(original with{Routes=original.Routes.Concat([original.Routes[0]]).ToArray()});Check(other.ReviewRouteHighlights.Count==0,"重复运输序号拒绝");
        Load(original with{Snapshot=snapshot with{RoadDetails=snapshot.RoadDetails.Concat([snapshot.RoadDetails[0] with{Id="parallel"}]).ToArray(),TotalRoads=5}});Check(other.ReviewRouteHighlights.Count==0,"平行路段歧义拒绝");
        Load(original with{Snapshot=snapshot with{RoadDetails=snapshot.RoadDetails.Select((r,i)=>i==1?r with{Id=snapshot.RoadDetails[0].Id}:r).ToArray()}});Check(other.ReviewRouteHighlights.Count==0,"重复路段编号拒绝");
        var reversed=snapshot.RoadDetails.Select((r,i)=>i==0?r with{From=r.To,To=r.From,Bidirectional=false}:r).ToArray();
        Load(original with{Snapshot=snapshot with{RoadDetails=reversed}});Check(other.ReviewRouteHighlights.Count==0,"单向逆行拒绝");
        Load(original with{Snapshot=snapshot with{RoadDetails=reversed.Select((r,i)=>i==0?r with{Bidirectional=true}:r).ToArray()}});Check(other.ReviewRouteHighlights.Count==4,"显式双向允许反向");
        Load(original with{Snapshot=snapshot with{Nodes=snapshot.Nodes.Concat([snapshot.Nodes[0] with{Id="DUP"}]).ToArray()}});Check(other.ReviewRouteHighlights.Count==0,"厂家编号重复拒绝");
        Load(original with{Snapshot=snapshot with{TotalRoads=5}});Check(other.ReviewRouteHighlights.Count==0,"缺损道路几何阻止误关联");
        Load(original with{Routes=original.Routes.Select((r,i)=>i==0?r with{Status="FAIL",Issues="模拟差异"}:r).ToArray(),ComparisonPassed=false});Check(other.ReviewRouteHighlights.Count==4&&other.ReviewRouteHighlights[0].Tooltip.Contains("FAIL"),"差异可看几何但保留FAIL说明");
        var large=snapshot with{RoadDetails=Enumerable.Range(0,3001).Select(i=>snapshot.RoadDetails[0] with{Id="ROAD"+i}).ToArray(),TotalRoads=3001};Load(original with{Snapshot=large});other.ReviewSearchMode="路段编号";other.ReviewSearchQuery="ROAD3000";other.SearchReviewMap();Check(other.ReviewRouteHighlights.Count==1,"底图预算外路段可搜索高亮");
        other.ReviewSearchQuery="ROAD";other.SearchReviewMap();Check(other.ReviewSearchResults.Count==200&&other.ReviewLocationMessage.Contains("3001"),"路段候选200上限总数提示");
        var middle=new MapNode("MID",2.5,0,MapNodeType.Waypoint,false,[],"LM-MID");
        var splitRoads=new[]{snapshot.RoadDetails[0] with{Id="FIRST",To="MID",Points=[new(0,0),new(2.5,0)]},snapshot.RoadDetails[0] with{Id="SECOND",From="MID",Points=[new(2.5,0),new(5,0)]}}.Concat(snapshot.RoadDetails.Skip(1)).ToArray();
        var splitRows=new[]{original.Routes[0] with{LogicalTo="MID",VendorTo="LM-MID"},original.Routes[0] with{Step=2,LogicalFrom="MID",VendorFrom="LM-MID"}}.Concat(original.Routes.Skip(1)).ToArray();
        Load(original with{Routes=splitRows,Snapshot=snapshot with{Nodes=snapshot.Nodes.Concat([middle]).ToArray(),RoadDetails=splitRoads,TotalRoads=5}});Check(other.ReviewRouteHighlights.Count==5,"四阶段中的多子段按顺序全部高亮");
        other.ReviewRouteRole=other.ReviewRouteRoles[1];Check(other.ReviewRouteHighlights.Count==0,"改变运输角色先清旧高亮");other.HighlightReviewRoutes();Check(other.ReviewRouteHighlights.Count==2,"单角色关联多个子段");
        Load(original with{Routes=splitRows.Select((r,i)=>i==1?r with{LogicalFrom="N_HOME",VendorFrom="LM0001"}:r).ToArray(),Snapshot=snapshot with{Nodes=snapshot.Nodes.Concat([middle]).ToArray(),RoadDetails=splitRoads,TotalRoads=5}});Check(other.ReviewRouteHighlights.Count==0,"子段端点不连续拒绝");
        Load(original with{Routes=Enumerable.Range(1,2049).Select(i=>original.Routes[0] with{Step=i}).ToArray()});Check(other.ReviewRouteHighlights.Count==0&&other.ReviewLocationMessage.Contains("2048"),"运输显示预算拒绝");
        other.ClearMapReviewCommand.Execute();Check(other.ReviewRouteHighlights.Count==0&&other.SelectedReviewRoute is null,"报告清空清高亮和运输选择");Check(((NoCalls)proxy).Calls==0,"路线高亮零后台调用");
        Complete(vm.ImportMapReviewAsync(Positive));vm.ReviewRouteRole="全部四段";vm.HighlightReviewRoutes();
    }
    public class NoCalls:DispatchProxy{public int Calls;protected override object? Invoke(MethodInfo? method,object?[]? args){Calls++;throw new InvalidOperationException("禁止后台调用");}}
    sealed class ImmediateReader(MapReviewReport report):IMapReviewReportReader{public Task<MapReviewReport> ReadAsync(string path,CancellationToken token)=>Task.FromResult(report);}
    sealed class DelayedReader:IMapReviewReportReader{public TaskCompletionSource<MapReviewReport> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);public Task<MapReviewReport> ReadAsync(string path,CancellationToken token)=>Completion.Task;}
}
