using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static partial class Program
{
    static void CheckBackendRouteMap(ManualTaskCreationService service,ManualTaskRoutePreviewRequest request,ActiveMapRuntime runtime,MapDefinition definition,ManualTaskRoutePreview preview,RoutePreviewChecker checker)
    {
        Require(preview.Map is {Nodes.Count:4,Edges.Count:3,Unit:"m"}&&preview.Map.MapId==preview.MapId&&preview.Map.VersionId==preview.MapVersionId&&preview.Map.ObservationId==preview.ObservationId&&Guid.TryParseExact(preview.ObservationId,"N",out _),"backend binds map identity and geometry to same observation");
        Require(preview.Map!.Nodes.Single(n=>n.Id=="PICK") is {VendorId:"LM-PICK",X:5,Y:0},"same-batch map retains business manufacturer IDs and local XY");
        var curved=definition with{Edges=definition.Edges.Select((e,i)=>i==0?e with{GeometryKind=MapEdgeGeometryKind.CubicBezier,ControlPoints=[new(1,2),new(4,2)]}:e).ToArray()};
        runtime.Replace(new("MAP","VERSION",curved,DateTimeOffset.Now));var curve=Complete(service.PreviewRoutesAsync(request,default));
        Require(curve.Map!.Edges.Single(e=>e.Id=="EDGE0") is {Kind:"CubicBezier",Points.Count:33}&&curve.Map.Edges.Single(e=>e.Id=="EDGE0").Points.Any(p=>p.Y>0),"backend samples actual Bezier rather than drawing chord");
        curved=definition with{Edges=definition.Edges.Select((e,i)=>i==0?e with{GeometryKind=MapEdgeGeometryKind.CircularArc,ControlPoints=[new(2.5,2.5)]}:e).ToArray()};
        runtime.Replace(new("MAP","VERSION",curved,DateTimeOffset.Now));var arc=Complete(service.PreviewRoutesAsync(request,default));Require(arc.Map!.Edges.Single(e=>e.Id=="EDGE0") is {Kind:"CircularArc",Points.Count:33},"backend retains actual circular arc samples");
        var extraEdges=Enumerable.Range(0,1100).Select(i=>definition.Edges[0] with{Id="BACKGROUND"+i,Distance=50}).ToArray();runtime.Replace(new("MAP","VERSION",definition with{Edges=definition.Edges.Concat(extraEdges).ToArray()},DateTimeOffset.Now));var large=Complete(service.PreviewRoutesAsync(request,default));
        Require(large.Map is {Edges.Count:1000,TotalEdges:1103}&&large.Segments.All(s=>large.Map.Edges.Any(e=>e.Id==s.EdgeId)),"background edge cap preserves all required route geometry first");
        var extraNodes=Enumerable.Range(0,2100).Select(i=>new MapNode("ISOLATED"+i,i,10,MapNodeType.Waypoint,true,[],"V"+i)).ToArray();runtime.Replace(new("MAP","VERSION",definition with{Nodes=definition.Nodes.Concat(extraNodes).ToArray()},DateTimeOffset.Now));large=Complete(service.PreviewRoutesAsync(request,default));Require(large.Map is {Nodes.Count:2048,TotalNodes:2104},"background node display is bounded and reports total");
        runtime.Replace(new("MAP","VERSION",definition with{Unit="mm"},DateTimeOffset.Now));Require(Complete(service.PreviewRoutesAsync(request,default)).Map is null,"non-metre backend map never guessed into visual coordinates");
        var budgetNodes=Enumerable.Range(0,2000).Select(i=>new MapNode("点"+new string('名',249)+i,100+i,20,MapNodeType.Waypoint,true,[],"厂"+new string('号',249)+i)).ToArray();
        var budgetEdges=Enumerable.Range(0,1000).Select(i=>new MapEdge("线"+new string('段',249)+i,budgetNodes[i*2].Id,budgetNodes[i*2+1].Id,1,1,true,true,[])).ToArray();
        runtime.Replace(new("MAP","VERSION",definition with{Nodes=definition.Nodes.Concat(budgetNodes).ToArray(),Edges=definition.Edges.Concat(budgetEdges).ToArray()},DateTimeOffset.Now));var bounded=Complete(service.PreviewRoutesAsync(request,default));Require(bounded.Map is null&&bounded.MapIssue.Contains("8MiB")&&bounded.Segments.Count==5,"oversized escaped-UTF8 map response omitted without losing route diagnostics");
        runtime.Replace(new("MAP","VERSION",definition,DateTimeOffset.Now));
        checker.Result=RoutePreviewChecker.Success with{Message=new string('文',4096),RouteChecks=RoutePreviewChecker.Success.RouteChecks.Select(r=>r with{Message=new string('因',4096)}).ToArray()};var limited=Complete(service.PreviewRoutesAsync(request,default));Require(limited.Message.Length<2100&&limited.Segments.All(s=>s.Message.Length<1100),"diagnostic text is bounded independently from control decisions");checker.Result=RoutePreviewChecker.Success;
        runtime.Replace(new("MAP","VERSION",definition,DateTimeOffset.Now));
    }
    static void CheckRouteMapUi(TaskCenterViewModel vm,AgvDispatch.Wpf.App app,string evidence,RoutePreviewClient client,ManualTaskRoutePreview positive,ManualTaskRoutePreview failed)
    {
        var latest=Complete(client.Service.PreviewRoutesAsync(new(positive.StationId,positive.VehicleId,positive.MapId,positive.MapVersionId,positive.ParameterRevision),default));positive=latest;failed=failed with{Map=latest.Map,ObservationId=latest.ObservationId};
        Require(vm.CreationMapRoutes.Count==5&&vm.CreationMapRoads.Count==3&&vm.CreationMapDots.Count==4,"current map scene consumes same-batch navigation geometry");
        var first=vm.CreationRouteSegments[0];vm.SelectedCreationRouteSegment=first;
        Require(vm.CreationRouteMapOpen&&!vm.CreationMapSelectedGeometry.Bounds.IsEmpty&&vm.CreationRouteMapMessage.Contains("LM-HOME → LM-PICK"),"table selection locates business and manufacturer exact endpoints");
        CheckRouteActions(vm,client,positive,app,evidence);
        vm.SelectedCreationRouteSegment=vm.CreationRouteSegments[0];
        var bounds=vm.CreationMapSelectedGeometry.Bounds;
        Require(Math.Abs((bounds.X+bounds.Width/2-450)*vm.CreationMapZoom+450+vm.CreationMapPanX-450)<1e-5&&Math.Abs((bounds.Y+bounds.Height/2-250)*vm.CreationMapZoom+250+vm.CreationMapPanY-250)<1e-5,"selected segment is centred with correct Y projection");
        vm.FitCreationRouteMapCommand.Execute();Require(vm.CreationMapZoom==1&&vm.CreationMapPanX==0&&vm.CreationMapPanY==0,"full-map reset does not mutate source");
        for(var i=0;i<30;i++)vm.ZoomInCreationRouteMapCommand.Execute();Require(vm.CreationMapZoom==64,"map zoom bounded above");for(var i=0;i<40;i++)vm.ZoomOutCreationRouteMapCommand.Execute();Require(vm.CreationMapZoom==.5,"map zoom bounded below");vm.FitCreationRouteMapCommand.Execute();
        client.Override=_=>System.Threading.Tasks.Task.FromResult(failed with{ObservedAt=DateTimeOffset.Now});Complete(vm.PreviewCreationRoutesAsync());
        Require(vm.LocateFailedCreationRouteCommand.CanExecute()&&vm.CreationMapRoutes.Last().Segment.Status=="不符"&&vm.CreationMapRoutes.Last().Color=="#EF4444","failed path stays red above overlapping unchecked return path");
        vm.LocateFailedCreationRouteCommand.Execute();Require(vm.SelectedCreationRouteSegment is {Sequence:5,Status:"不符"}&&vm.CreationRouteMapMessage.Contains("1303不是直接路径"),"locate first failure points to original failed adjacent segment");
        RenderCurrentRouteMap(vm,app,evidence,"failure-map-light",1000,620,true);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var form=new TaskCenterView{DataContext=vm,Width=1280,Height=900};form.Measure(new Size(1280,900));form.Arrange(new Rect(0,0,1280,900));form.UpdateLayout();((TabItem)form.FindName("CreationRouteDetailsTab")).IsSelected=true;form.UpdateLayout();
        var grid=(DataGrid)form.FindName("CreationRouteSegmentGrid");grid.SelectedItem=vm.CreationRouteSegments[1];form.UpdateLayout();Require(ReferenceEquals(grid.SelectedItem,vm.SelectedCreationRouteSegment)&&((TabItem)form.FindName("CreationRouteMapTab")).IsSelected,"formal creation table selected-item binding switches to map tab");
        var theme=app.Resources.MergedDictionaries.FirstOrDefault(d=>d.Source?.OriginalString.Contains("Themes/Light.xaml")==true);if(theme is not null){theme.Source=new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/Dark.xaml");vm.LocateFailedCreationRouteCommand.Execute();RenderCurrentRouteMap(vm,app,evidence,"failure-map-dark-small",760,500,false);theme.Source=new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/Light.xaml");}
        void Load(ManualTaskRoutePreview result){client.Override=_=>System.Threading.Tasks.Task.FromResult(result with{ObservedAt=DateTimeOffset.Now});Complete(vm.PreviewCreationRoutesAsync());}
        Load(positive with{Map=positive.Map! with{VersionId="OTHER"}});Require(vm.CreationMapRoutes.Count==0&&vm.CreationRouteLegs.Count==4,"wrong map version rejects map but retains diagnostic table");
        Load(positive with{Map=positive.Map! with{ObservationId=Guid.NewGuid().ToString("N")}});Require(vm.CreationMapRoutes.Count==0,"cross-observation geometry rejected");
        Load(positive with{Map=null,MapIssue="旧后台未提供导航几何"});Require(vm.CreationMapRoads.Count==0&&vm.CreationRouteMapMessage.Contains("旧后台"),"missing geometry never falls back to history map");
        Load(positive with{Map=positive.Map! with{Nodes=positive.Map.Nodes.Concat([positive.Map.Nodes[0]]).ToArray(),TotalNodes=5}});Require(vm.CreationMapDots.Count==0,"duplicate business nodes cannot locate ambiguous objects");
        Load(positive with{Map=positive.Map! with{Edges=positive.Map.Edges.Select((e,i)=>i==0?e with{Kind="CubicBezier"}:e).ToArray()}});Require(vm.CreationMapRoads.Count==0,"two-point chord cannot masquerade as a sampled Bezier");
        Load(positive with{Map=positive.Map! with{Unit="mm"}});Require(vm.CreationMapDots.Count==0,"client refuses guessed units");
        Load(positive with{Map=positive.Map! with{Nodes=positive.Map.Nodes.Select((n,i)=>i==0?n with{X=double.NaN}:n).ToArray()}});Require(vm.CreationMapRoads.Count==0,"client refuses non-finite coordinates");
        Load(positive with{Map=positive.Map! with{Edges=positive.Map.Edges.Concat([positive.Map.Edges[0]]).ToArray(),TotalEdges=4}});Require(vm.CreationMapRoads.Count==0,"client refuses duplicate edges");
        Load(positive with{Map=positive.Map! with{Edges=positive.Map.Edges.Select((e,i)=>i==0?e with{Points=[new(999,0),new(5,0)]}:e).ToArray()}});Require(vm.CreationMapRoads.Count==0,"wrong geometry endpoint never becomes guessed straight line");
        Load(positive with{Map=positive.Map! with{Nodes=positive.Map.Nodes.Select((n,i)=>i==0?n with{VendorId="WRONG"}:n).ToArray()}});vm.LocateCreationRouteSegment(vm.CreationRouteSegments[0]);Require(vm.SelectedCreationRouteSegment is null&&vm.CreationMapSelectedGeometry.Bounds.IsEmpty,"mismatched vendor endpoint cannot locate a different object");
        Load(positive with{Map=positive.Map! with{Edges=positive.Map.Edges.Skip(1).ToArray()}});vm.LocateCreationRouteSegment(vm.CreationRouteSegments[0]);Require(vm.CreationMapSelectedGeometry.Bounds.IsEmpty&&vm.CreationRouteMapMessage.Contains("缺少"),"missing required curve does not draw replacement chord");
        Load(positive with{Map=positive.Map! with{Edges=positive.Map.Edges.Select(e=>e with{Bidirectional=false}).ToArray()}});vm.LocateCreationRouteSegment(vm.CreationRouteSegments[3]);Require(vm.SelectedCreationRouteSegment is null,"reverse route requires explicit bidirectional edge");
        Load(positive);var stale=vm.CreationRouteSegments[0];client.Override=null;Complete(vm.PreviewCreationRoutesAsync());vm.LocateCreationRouteSegment(stale);Require(vm.SelectedCreationRouteSegment is null,"old same-value segment cannot select new observation");
        vm.LocateCreationRouteSegment(vm.CreationRouteSegments[0] with{});Require(vm.CreationMapSelectedGeometry.Bounds.IsEmpty,"forged same-value row rejected");
        vm.ClearCreationRoutesCommand.Execute();Require(vm.CreationMapRoads.Count==0&&vm.CreationMapRoutes.Count==0&&vm.SelectedCreationRouteSegment is null&&!vm.LocateFailedCreationRouteCommand.CanExecute(),"clear removes map geometry selection and failure command");
        client.Override=null;Complete(vm.PreviewCreationRoutesAsync());typeof(TaskCenterViewModel).GetField("_routePreviewExpires",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(vm,DateTimeOffset.Now.AddSeconds(-1));vm.ExpireCreationRoutePreview();Require(vm.CreationMapRoads.Count==0&&vm.CreationMapDots.Count==0&&!vm.CreationRouteActionDetails.Contains("JackLoad"),"expiry clears standalone shared map and action state");
        client.Override=null;Complete(vm.PreviewCreationRoutesAsync());var options=Complete(client.Service.GetOptionsAsync(default));
        client.ReadOptions=()=>Task.FromResult(options with{MapRuntimeId=Guid.NewGuid().ToString("N")});Complete(vm.RecheckCreationRouteMapContextAsync(default));Require(vm.CreationMapRoutes.Count==0,"same map ID and version with replaced runtime instance clears map");
        client.ReadOptions=null;Complete(vm.PreviewCreationRoutesAsync());client.ReadOptions=()=>Task.FromResult(options with{ParameterRevision="CHANGED"});Complete(vm.RecheckCreationRouteMapContextAsync(default));Require(vm.CreationMapRoads.Count==0,"background parameter revision change clears map");
        client.ReadOptions=null;Complete(vm.PreviewCreationRoutesAsync());client.ReadOptions=()=>Task.FromResult(options with{Vehicles=options.Vehicles.Select(v=>v with{Available=false}).ToArray()});Complete(vm.RecheckCreationRouteMapContextAsync(default));Require(vm.CreationMapDots.Count==0,"background vehicle condition change clears map");
        client.ReadOptions=null;Complete(vm.PreviewCreationRoutesAsync());client.ReadOptions=()=>Task.FromResult(options with{Stations=options.Stations.Select(s=>s with{Available=false}).ToArray()});Complete(vm.RecheckCreationRouteMapContextAsync(default));Require(vm.CreationMapRoutes.Count==0,"background station condition change clears map");
        client.ReadOptions=null;Complete(vm.PreviewCreationRoutesAsync());client.ReadOptions=()=>Task.FromException<ManualTaskCreationOptions>(new IOException("simulated backend disconnect"));Complete(vm.RecheckCreationRouteMapContextAsync(default));Require(vm.CreationMapRoads.Count==0,"context read failure clears old map");
        client.ReadOptions=null;var blocked=positive with{Verified=false,ObservedAt=DateTimeOffset.Now,Map=positive.Map! with{SourceOptionsAvailable=false,SourceStationAvailable=false}};Load(blocked);client.ReadOptions=()=>Task.FromResult(options with{Available=false,Stations=options.Stations.Select(s=>s with{Available=false}).ToArray()});Complete(vm.RecheckCreationRouteMapContextAsync(default));Require(vm.CreationMapRoads.Count>0,"known unchanged blocked condition remains available for diagnosis until expiry");client.ReadOptions=null;
        client.ReadOptions=null;Complete(vm.PreviewCreationRoutesAsync());var delayed=new TaskCompletionSource<ManualTaskCreationOptions>(TaskCreationOptions.RunContinuationsAsynchronously);client.ReadOptions=()=>delayed.Task;var old=vm.RecheckCreationRouteMapContextAsync(default);client.ReadOptions=null;Complete(vm.PreviewCreationRoutesAsync());delayed.SetResult(options with{MapVersionId="OLD"});Complete(old);Require(vm.CreationMapRoutes.Count==5,"late background context check cannot clear newer map observation");
        Require(client.Writes==0,"map selection zoom fit and failure location never invoke controls");
    }
    static void RenderCurrentRouteMap(TaskCenterViewModel vm,AgvDispatch.Wpf.App app,string evidence,string name,int width,int height,bool hitTest)
    {
        var view=new TaskCreationRouteMapView{DataContext=vm,Width=width,Height=height,Background=(Brush)app.FindResource("AppBackgroundBrush")};view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        if(hitTest)
        {
            var host=new Window{Content=view,Width=width+20,Height=height+45,ShowActivated=false,ShowInTaskbar=false,Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual};
            try
            {
                host.Show();view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                var paths=MapVisualChildren(view).OfType<System.Windows.Shapes.Path>().Where(p=>p.DataContext is TaskCreationRouteMapStroke&&p.Cursor==Cursors.Hand).ToArray();
                var path=paths.First(p=>{var b=p.Data.Bounds;var point=p.TransformToAncestor(view).Transform(new Point(b.Left+b.Width/2,b.Top+b.Height/2));return ReferenceEquals(view.InputHitTest(point),p);});
                var stroke=(TaskCreationRouteMapStroke)path.DataContext;path.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.MouseDownEvent});view.UpdateLayout();
                Require(ReferenceEquals(vm.SelectedCreationRouteSegment,stroke.Segment),"actual hosted map input hit and path click select current row");
            }
            finally{host.Content=null;host.Close();}
        }
        var canvas=MapVisualChildren(view).OfType<Canvas>().First(c=>c.Width==900&&c.Height==500&&c.ClipToBounds);Require(canvas.TransformToAncestor(view).TransformBounds(new Rect(0,0,900,500)).Height>160,name+" map remains readable");
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(evidence,name+".png"));png.Save(file);
    }
    static IEnumerable<DependencyObject> MapVisualChildren(DependencyObject item){for(var i=0;i<VisualTreeHelper.GetChildrenCount(item);i++){var child=VisualTreeHelper.GetChild(item,i);yield return child;foreach(var nested in MapVisualChildren(child))yield return nested;}}
}
