using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;

internal static partial class Program
{
    static bool PointsRight(Geometry geometry)
    {
        var figure=geometry.GetFlattenedPathGeometry().Figures[0];
        return figure.StartPoint.X>figure.Segments.OfType<PolyLineSegment>().SelectMany(s=>s.Points).Average(p=>p.X);
    }
    static void CheckSegments(ConnectionSettingsViewModel vm)
    {
        Complete(vm.ImportMapReviewAsync(Positive));vm.HighlightReviewRoutes();var original=vm.MapReview!;var snapshot=original.Snapshot!;
        Check(vm.ReviewRouteSegments.Count==4&&vm.ReviewRouteHighlights.All(o=>!o.Arrows.Bounds.IsEmpty),"四阶段子段明细和方向箭头");
        var vertical=vm.ReviewRouteHighlights[1].Arrows.GetFlattenedPathGeometry().Figures[0];Check(vertical.StartPoint.Y<vertical.Segments.OfType<PolyLineSegment>().SelectMany(s=>s.Points).Average(p=>p.Y),"向上运输箭头按Y轴转换朝终点");
        Check(PointsRight(vm.ReviewRouteHighlights[0].Arrows),"本地Y轴转换后水平运输箭头朝终点");
        vm.SelectReviewSegment(vm.ReviewRouteSegments[1]);Check(vm.ReviewSegmentDetails.Contains("LM0101 → LM0201")&&vm.ReviewSegmentDetails.Contains("直线")&&!vm.ReviewSelectedRoadGeometry.Bounds.IsEmpty,"子段选择显示厂家端点几何并描边");
        Check(vm.ReviewSegmentDetails.Contains("(5, 0)m")&&vm.ReviewSegmentDetails.Contains("(5, 5)m"),"属性使用本地米制起终点");
        Render(vm,"segment-grid-binding",tab:"地图定位与搜索");Render(vm,"segment-path-click",tab:"地图定位与搜索");
        var old=vm.ReviewRouteSegments[0];vm.HighlightReviewRoutes();vm.SelectReviewSegment(old);Check(vm.SelectedReviewSegment is null&&vm.ReviewSelectedRoadGeometry.Bounds.IsEmpty,"旧同值子段不复用");
        vm.SelectReviewSegment(vm.ReviewRouteSegments[0] with{});Check(vm.SelectedReviewSegment is null,"外来子段拒绝");
        vm.ReviewSearchMode="路段编号";vm.ReviewSearchQuery="E_HOME_PICK";vm.SearchReviewMap();Check(vm.ReviewRouteHighlights[0].Arrows.GetFlattenedPathGeometry().Figures.Count==2&&vm.ReviewRouteSegments[0].Direction.Contains("双向"),"双向道路搜索显示双箭头");
        vm.SelectReviewSegment(vm.ReviewRouteSegments[0]);Check(vm.ReviewSegmentDetails.Contains("非任务行驶方向")&&vm.ReviewSegmentDetails.Contains("未关联运输结果"),"搜索道路不冒充运输方向和执行结果");
        vm.ReviewSearchMode="业务编号";vm.ReviewSearchQuery="N_PICK";vm.SearchReviewMap();Check(vm.ReviewRouteSegments.Count==0&&vm.SelectedReviewSegment is null,"节点搜索清空子段和描边");
        var proxy=DispatchProxy.Create<ITrafficControlClient,NoCalls>();var other=new ConnectionSettingsViewModel(proxy);
        void Load(MapReviewReport r){other.MapReviewReader=new ImmediateReader(r);Complete(other.ImportMapReviewAsync("synthetic-test"));other.HighlightReviewRoutes();}
        var reversed=snapshot.RoadDetails.Select((r,i)=>i==0?r with{From=r.To,To=r.From,Points=r.Points.Reverse().ToArray(),Bidirectional=true}:r).ToArray();
        Load(original with{Snapshot=snapshot with{RoadDetails=reversed}});Check(other.ReviewRouteSegments[0].Direction.Contains("反向")&&PointsRight(other.ReviewRouteHighlights[0].Arrows),"双向反向运输箭头按实际运输终点");
        var root=JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Positive)!,"inputs/local_map.json")))!;
        root["edges"]![0]!["geometryKind"]="CubicBezier";root["edges"]![0]!["controlPoints"]=JsonNode.Parse("[{\"x\":1,\"y\":2},{\"x\":4,\"y\":2}]");
        using(var doc=JsonDocument.Parse(root.ToJsonString()))
        {
            var curved=MapReviewSnapshot.Read(doc.RootElement,"curve-sha",default);Load(original with{Snapshot=curved});
            Check(curved.RoadDetails[0].GeometryKind==MapEdgeGeometryKind.CubicBezier&&curved.RoadDetails[0].ControlPoints.Count==2,"快照保留Bezier类型和控制点");
            other.SelectReviewSegment(other.ReviewRouteSegments[0]);Check(other.ReviewSegmentDetails.Contains("三阶Bezier")&&other.ReviewSegmentDetails.Contains("(1, 2)m")&&PointsRight(other.ReviewRouteHighlights[0].Arrows),"Bezier箭头及控制点明细");
        }
        root["edges"]![0]!["geometryKind"]="CircularArc";root["edges"]![0]!["controlPoints"]=JsonNode.Parse("[{\"x\":2.5,\"y\":2.5}]");
        using(var doc=JsonDocument.Parse(root.ToJsonString())){Load(original with{Snapshot=MapReviewSnapshot.Read(doc.RootElement,"arc-sha",default)});other.SelectReviewSegment(other.ReviewRouteSegments[0]);Check(other.ReviewSegmentDetails.Contains("圆弧")&&other.ReviewSegmentDetails.Contains("(2.5, 2.5)m")&&PointsRight(other.ReviewRouteHighlights[0].Arrows),"圆弧过弧点明细");}
        var overlap=JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Positive)!,"inputs/local_map.json")))!;
        overlap["nodes"]![3]!["x"]=0;overlap["nodes"]![3]!["y"]=0;
        using(var doc=JsonDocument.Parse(overlap.ToJsonString()))
        {
            Load(original with{Snapshot=MapReviewSnapshot.Read(doc.RootElement,"overlap-sha",default)});
            Check(other.ReviewRouteHighlights[0].Geometry.Bounds==other.ReviewRouteHighlights[3].Geometry.Bounds,"合成重叠道路保持各自运输子段");
            other.SelectReviewSegment(other.ReviewRouteSegments[0]);var first=other.SelectedReviewSegment;other.SelectReviewSegment(other.ReviewRouteSegments[3]);
            Check(!ReferenceEquals(first,other.SelectedReviewSegment)&&other.ReviewSegmentDetails.Contains("E_DUMP_PICK")&&other.ReviewSegmentDetails.Contains("倒料 → 取货"),"重叠道路从子段列表明确选取各自属性");
        }
        Load(original with{Routes=original.Routes.Select((r,i)=>i==0?r with{Status="FAIL",Issues="模拟映射差异"}:r).ToArray(),ComparisonPassed=false});other.SelectReviewSegment(other.ReviewRouteSegments[0]);Check(other.ReviewSegmentDetails.Contains("FAIL")&&other.ReviewSegmentDetails.Contains("模拟映射差异"),"FAIL与原差异保留在属性");
        var degenerate=snapshot.RoadDetails.Select((r,i)=>i==0?r with{Points=[new(0,0),new(0,0),new(0,0)]}:r).ToArray();Load(original with{Snapshot=snapshot with{RoadDetails=degenerate}});Check(other.ReviewRouteHighlights[0].Arrows.Bounds.IsEmpty,"重合点不生成伪方向");
        var repeated=snapshot.RoadDetails.Select((r,i)=>i==0?r with{Points=[new(0,0),new(0,0),new(5,0),new(5,0)]}:r).ToArray();Load(original with{Snapshot=snapshot with{RoadDetails=repeated}});Check(!other.ReviewRouteHighlights[0].Arrows.Bounds.IsEmpty,"含重复点仍能确定有效方向");
        var stale=other.ReviewRouteSegments[0];other.ReviewRouteRole=other.ReviewRouteRoles[1];Check(other.ReviewRouteSegments.Count==0&&other.SelectedReviewSegment is null,"角色切换清子段属性");other.SelectReviewSegment(stale);Check(other.SelectedReviewSegment is null,"角色切换旧段失效");
        other.ClearMapReviewCommand.Execute();Check(other.ReviewRouteSegments.Count==0&&other.ReviewSelectedRoadGeometry.Bounds.IsEmpty,"清报告清子段描边");Check(((NoCalls)proxy).Calls==0,"箭头明细联动零后台调用");
        Complete(vm.ImportMapReviewAsync(Positive));vm.HighlightReviewRoutes();vm.SelectReviewSegment(vm.ReviewRouteSegments[2]);
    }
    static void CheckSegmentUi(ConnectionSettingsViewModel vm,AgvDispatch.Wpf.Views.ConnectionSettingsView view,string name)
    {
        if(name=="routes-dark-small")
        {
            var grid=(DataGrid)view.FindName("ReviewSegmentGrid");
            Check(grid.ActualHeight>90,"1100x700子段表保留可选行空间");
            var row=grid.ItemContainerGenerator.ContainerFromItem(vm.SelectedReviewSegment) as DataGridRow;
            Check(row is not null&&row.TransformToAncestor(grid).Transform(new Point(0,0)).Y>=0&&row.TransformToAncestor(grid).Transform(new Point(0,0)).Y<grid.ActualHeight-18,"画布选中子段滚动到表格可见行");
        }
        if(name=="segment-grid-binding")
        {
            var grid=(DataGrid)view.FindName("ReviewSegmentGrid");grid.SelectedItem=vm.ReviewRouteSegments[2];
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            Check(ReferenceEquals(vm.SelectedReviewSegment,grid.SelectedItem)&&vm.ReviewSegmentDetails.Contains("E_WAIT_DUMP"),"正式子段Grid更新属性及地图");
        }
        if(name=="segment-path-click")
        {
            var host=new Window{Content=view,Width=1500,Height=900,ShowActivated=false,ShowInTaskbar=false,Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual};
            host.Show();view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            try
            {
            var path=Descendants(view).OfType<System.Windows.Shapes.Path>().First(p=>
            {
                if(p.DataContext is not ReviewRouteOverlay||p.Cursor!=Cursors.Hand)return false;
                var bounds=p.Data.Bounds;var center=p.TransformToAncestor(view).Transform(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2));
                return ReferenceEquals(view.InputHitTest(center),p);
            });
            Check(true,"视图实际命中高亮道路点击区域");
            var overlay=(ReviewRouteOverlay)path.DataContext;
            path.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.MouseDownEvent});
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            Check(ReferenceEquals(vm.SelectedReviewSegment,overlay.Segment)&&vm.ReviewSegmentsOpen,"正式高亮Path点击联动子段选择");
            }
            finally{host.Content=null;host.Close();}
        }
    }
}
