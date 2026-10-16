using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class AcceptanceProgram
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    static readonly List<object> Results=[];
    static string Output="";
    [STAThread] static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Output=Path.GetFullPath("docs/commissioning/evidence/20261014-robokit-map-acceptance");Directory.CreateDirectory(Output);
        try
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            foreach(var uri in new[]{"pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml","pack://application:,,,/HandyControl;component/Themes/Theme.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Themes/Light.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/Typography.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/Controls.xaml"})app.Resources.MergedDictionaries.Add(new(){Source=new Uri(uri)});
            var sources=Path.GetFullPath("docs/vendor/robokit/map-format-20261014/smap-master/maps");
            foreach(var file in Directory.GetFiles(sources,"*.smap").OrderBy(x=>x))CheckMap(file,Path.GetFileNameWithoutExtension(file));
            CheckMap(Path.GetFullPath("docs/examples/robokit-map-acceptance/mixed-types.smap"),"mixed-types");
            Complete(MapWireChecks.Run(Output,(ok,name)=>Check(ok,name,"protocol")));
            File.WriteAllText(Path.Combine(Output,"results.json"),JsonSerializer.Serialize(new{Passed=Results.Count,Failed=0,PhysicalDevice=false,Results},Json));
            Console.WriteLine($"PASS {Results.Count} map assertions; no physical upload");return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(Output,"failure.txt"),ex.ToString());Console.Error.WriteLine(ex);return 1;}
    }
    static void Check(bool ok,string name,string map){if(!ok)throw new Exception(map+": "+name);Results.Add(new{Map=map,Name=name,Status="PASS"});Console.WriteLine("PASS "+map+" "+name);}
    static void Complete(Task task){while(!task.IsCompleted)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);task.GetAwaiter().GetResult();}
    static IEnumerable<JsonElement> List(JsonElement root,string key)=>root.TryGetProperty(key,out var p)?p.EnumerateArray().ToArray():[];
    static double Num(JsonElement root,string key){var n=root.TryGetProperty(key,out var p)?p.GetDouble():0;if(!double.IsFinite(n))throw new InvalidDataException("非有限坐标");return n;}
    static string Str(JsonElement root,string key)=>root.TryGetProperty(key,out var p)?p.GetString()??"":"";
    static void CheckMap(string file,string id)
    {
        using var doc=JsonDocument.Parse(File.ReadAllBytes(file));var root=doc.RootElement;var header=root.GetProperty("header");
        Check(Str(header,"mapType")=="2D-Map" && Str(header,"version").StartsWith("1.0."),"2D JSON及文档1.0.x样例版本",id);
        var min=header.GetProperty("minPos");var max=header.GetProperty("maxPos");var minX=Num(min,"x")-1;var minY=Num(min,"y")-1;var w=Num(max,"x")-minX+1;var h=Num(max,"y")-minY+1;
        if(w<=0 || h<=0)throw new InvalidDataException("地图边界无效");
        MapPoint2D Translate(JsonElement p)=>new(Num(p,"x")-minX,Num(p,"y")-minY);
        var classes=new Dictionary<string,string>();var nodes=new List<MapNode>();
        foreach(var p in List(root,"advancedPointList"))
        {
            var site=Str(p,"instanceName");var kind=Str(p,"className");if(string.IsNullOrWhiteSpace(site) || classes.ContainsKey(site))throw new InvalidDataException("空/重复厂家站点");classes.Add(site,kind);
            var pos=Translate(p.GetProperty("pos"));var type=kind switch{"LocationMark" or "LandMark"=>MapNodeType.Waypoint,"ActionPoint" or "TransferLocation" or "WorkLocation" or "WorkingLocation"=>MapNodeType.Station,"ParkPoint"=>MapNodeType.WaitingPoint,"ChargePoint"=>MapNodeType.ChargingPoint,_=>throw new NotSupportedException("未核定高级点类型："+kind)};
            nodes.Add(new("BUS-"+site,pos.X,pos.Y,type,false,[],site));
        }
        var bySite=nodes.ToDictionary(n=>n.AgvSiteId!,StringComparer.Ordinal);var edges=new List<MapEdge>();var unsupported=new List<string>();
        foreach(var c in List(root,"advancedCurveList"))
        {
            var kind=Str(c,"className");var start=c.GetProperty("startPos");var end=c.GetProperty("endPos");var s=Str(start,"instanceName");var t=Str(end,"instanceName");
            if(!bySite.ContainsKey(s)||!bySite.ContainsKey(t))throw new InvalidDataException("路径引用不存在的高级点");
            foreach(var endpoint in new[]{start,end})if(endpoint.TryGetProperty("pos",out var ep)){var translated=Translate(ep);var known=bySite[Str(endpoint,"instanceName")];if(Math.Abs(translated.X-known.X)>0.001 || Math.Abs(translated.Y-known.Y)>0.001)throw new InvalidDataException("端点内嵌坐标与站点不一致");}
            if(kind is not ("StraightPath" or "BezierPath")){unsupported.Add(kind+":"+Str(c,"instanceName"));continue;}
            var controls=kind=="BezierPath"?new[]{Translate(c.GetProperty("controlPos1")),Translate(c.GetProperty("controlPos2"))}:null;
            var edge=new MapEdge("PATH-"+edges.Count,bySite[s].Id,bySite[t].Id,1,1,false,false,[]){GeometryKind=controls is null?MapEdgeGeometryKind.Line:MapEdgeGeometryKind.CubicBezier,ControlPoints=controls};
            if(!MapEdgeGeometry.TryValidate(edge,bySite[s],bySite[t],out var error))throw new InvalidDataException(error);
            edges.Add(edge with{Distance=MapEdgeGeometry.Length(edge,bySite[s],bySite[t])});
        }
        var definition=new MapDefinition("Robokit示例 "+id,file,"SmapWorldTranslated","m",nodes,edges,[]){EditorSettings=new(w,h,Math.Max(1,w/20),true,false,false)};
        var package=Path.GetFullPath("docs/examples/robokit-map-acceptance/软件可打开/"+id);Directory.CreateDirectory(package);
        var normalized=Path.Combine(package,"navigation.json");File.WriteAllText(normalized,JsonSerializer.Serialize(definition,Json));
        var imported=new MapImportService().Import(new(normalized));
        Check(imported.Nodes.Count==List(root,"advancedPointList").Count() && imported.Edges.Count+unsupported.Count==List(root,"advancedCurveList").Count(),"高级点/路径数量守恒，未支持几何单列",id);
        Check(imported.Nodes.All(n=>n.Id!=n.AgvSiteId && n.AgvSiteId is not null && !n.Enabled) && imported.Edges.All(e=>!e.Enabled),"业务与厂家编号独立且预览不参与调度",id);
        Check(List(root,"normalPosList").Count()+List(root,"normalLineList").Count()>0 && imported.Resources.Count==0,"普通点云/绘图线/区域没有自动转为路权资源",id);
        foreach(var n in imported.Nodes){var original=List(root,"advancedPointList").Single(p=>Str(p,"instanceName")==n.AgvSiteId).GetProperty("pos");if(Math.Abs(n.X+minX-Num(original,"x"))>1e-8 || Math.Abs(n.Y+minY-Num(original,"y"))>1e-8)throw new Exception("坐标逆变换失败");}
        Check(true,"源米制坐标平移后可逆，映射关系留档",id);
        var bg=Path.Combine(package,"source-background.png");RenderBackground(root,minX,minY,w,h,bg);
        var store=new LocalMapEditorStore(Path.Combine(Output,"isolated-store-"+id));var workspace=store.Create(new(definition.Name,"Robokit原图转换预览，站点和路径停用",file,definition.CoordinateSystem,"m",1));
        workspace=workspace with{Project=workspace.Project with{PreviewPngPath=bg,CadViewBox=new(0,0,w,h,900,520),UnderlayStatus=CadUnderlayJobStatus.Ready,UnderlayMessage="Smap点云/普通线/区域仅供显示"},Draft=workspace.Draft with{Definition=definition,Validation=store.Validate(definition)}};
        var mapFile=Path.Combine(package,id+".agvmap.json");Complete(store.ExportAsync(workspace,mapFile));
        var proxy=System.Reflection.DispatchProxy.Create<ITrafficControlClient,RejectingTrafficProxy>();var spy=(RejectingTrafficProxy)proxy;var vm=new MapManagementViewModel(proxy){LocalStore=store};Complete(vm.OpenLocalFileAsync(mapFile));
        Check(vm.IsOfflineEditing && vm.Nodes.Count==nodes.Count && vm.Edges.Count==edges.Count && spy.Calls==0,"真实软件本地打开地图，后台调用为0",id);
        Check(vm.UnderlayImage is not null,"实际编辑器显示原始点云/绘图背景",id);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var view=new MapManagementView{DataContext=vm,Width=1440,Height=900,Background=(Brush)System.Windows.Application.Current.FindResource("AppBackgroundBrush")};
        view.Measure(new Size(1440,900));view.Arrange(new Rect(0,0,1440,900));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
        Check(vm.PreviewNodes.Count==nodes.Count,"实际Canvas导航点显示数量一致",id);
        foreach(var marker in vm.PreviewNodes){var n=nodes.Single(n=>n.Id==marker.Id);if(Math.Abs(marker.Left+7-n.X/w*900)>1e-6 || Math.Abs(marker.Top+7-(1-n.Y/h)*520)>1e-6)throw new Exception("显示站点与原图坐标未对齐");}
        Check(true,"Canvas站点像素位置与原图米制坐标一致",id);
        var image=new RenderTargetBitmap(1440,900,96,96,PixelFormats.Pbgra32);image.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(Path.Combine(Output,"map-"+id+".png")))encoder.Save(stream);
        Check(File.Exists(Path.Combine(Output,"map-"+id+".png")),"真实WPF地图页渲染截图留存",id);vm.DeactivateEditor();
        File.WriteAllText(Path.Combine(package,"mapping-review.json"),JsonSerializer.Serialize(new{Source=file,SourceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),SourceFormatVersion=Str(header,"version"),Origin="official_example_or_explicit_synthetic_not_vehicle",CoordinateTransform=new{LocalX="SourceX-MinX",LocalY="SourceY-MinY",MinX=minX,MinY=minY},NavigationEnabled=false,ManufacturerUploadEnabled=false,PointClasses=classes,UnsupportedGeometry=unsupported,OrdinaryPoints=List(root,"normalPosList").Count(),OrdinaryLines=List(root,"normalLineList").Count(),AdvancedAreas=List(root,"advancedAreaList").Count()},Json));
    }
    static void RenderBackground(JsonElement root,double minX,double minY,double width,double height,string path)
    {
        const int w=900,h=520;var pixels=new byte[w*h*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=247;pixels[i+1]=247;pixels[i+2]=247;pixels[i+3]=255;}
        Point P(JsonElement p)=>new((Num(p,"x")-minX)/width*w,(1-(Num(p,"y")-minY)/height)*h);
        foreach(var name in new[]{"normalPosList","rssiPosList","reflectorPosList","tagPosList"})foreach(var p in List(root,name)){var q=P(p);var x=(int)q.X;var y=(int)q.Y;if(x>=0&&x<w&&y>=0&&y<h){var i=(y*w+x)*4;pixels[i]=110;pixels[i+1]=110;pixels[i+2]=110;}}
        var bitmap=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4);var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawImage(bitmap,new Rect(0,0,w,h));foreach(var l in List(root,"normalLineList"))dc.DrawLine(new Pen(Brushes.Gray,1),P(l.GetProperty("startPos")),P(l.GetProperty("endPos")));
            foreach(var l in List(root,"advancedLineList")){var line=l.GetProperty("line");dc.DrawLine(new Pen(Brushes.IndianRed,2),P(line.GetProperty("startPos")),P(line.GetProperty("endPos")));}
            foreach(var area in List(root,"advancedAreaList")){var points=List(area,"posGroup").ToArray();if(points.Length<3)continue;var geometry=new StreamGeometry();using(var g=geometry.Open()){g.BeginFigure(P(points[0]),true,true);g.PolyLineTo(points.Skip(1).Select(P).ToArray(),true,false);}dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(35,40,130,210)),new Pen(Brushes.SteelBlue,1),geometry);}
        }
        var image=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);image.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);png.Save(file);
    }
}
