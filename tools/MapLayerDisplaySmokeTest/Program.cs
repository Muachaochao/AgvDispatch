using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class LayerTest
{
    static readonly string Evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-map-layers");
    static readonly List<object> Results=[];
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
    static void Complete(Task task){var until=DateTime.UtcNow.AddSeconds(45);while(!task.IsCompleted&&DateTime.UtcNow<until)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
    static string Geometry(MapManagementViewModel vm)=>JsonSerializer.Serialize(new{Nodes=vm.Nodes.Select(n=>new{n.Id,n.AgvSiteId,n.X,n.Y,n.Enabled}),Edges=vm.Edges.Select(e=>new{e.Id,e.From,e.To,e.Enabled})});
    static void Capture(MapManagementViewModel vm,string file,int width=1600,int height=1000)
    {
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
        var view=new MapManagementView{DataContext=vm,Width=width,Height=height,Background=(Brush)System.Windows.Application.Current.FindResource("AppBackgroundBrush")};
        view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        if(view.FindName("InspectorTabs") is TabControl tabs)foreach(TabItem item in tabs.Items)if(item.Header?.ToString()=="图层")tabs.SelectedItem=item;
        Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(Evidence,file));png.Save(stream);
    }
    [STAThread] static int Main()
    {
        Directory.CreateDirectory(Evidence);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            foreach(var path in new[]{"HandyControl;component/Themes/SkinDefault.xaml","HandyControl;component/Themes/Theme.xaml","AgvDispatch.Wpf;component/Themes/Light.xaml","AgvDispatch.Wpf;component/Styles/Typography.xaml","AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml","AgvDispatch.Wpf;component/Styles/Controls.xaml"})app.Resources.MergedDictionaries.Add(new(){Source=new Uri("pack://application:,,,/"+path)});
            var proxy=System.Reflection.DispatchProxy.Create<ITrafficControlClient,RejectingTrafficProxy>();
            var store=new LocalMapEditorStore(Path.Combine(Evidence,"private-store-"+Guid.NewGuid().ToString("N")));
            var vm=new MapManagementViewModel(proxy){LocalStore=store};
            foreach(var name in new[]{"6","7"})
            {
                var source=$"docs/vendor/robokit/map-format-20261014/smap-master/maps/{name}.smap";var hash=SHA256.HashData(File.ReadAllBytes(source));
                Complete(vm.OpenLocalFileAsync(source));var geometry=Geometry(vm);var count=vm.Nodes.Count;
                Check(vm.HasSmapBackgroundLayers&&vm.SmapScanImage is not null&&vm.SmapAnnotationImage is not null,name+" 新导入生成两个透明底图通道");
                Check(vm.PreviewGridLines.Count>10&&vm.PreviewGridLines.Count<=484,name+" 带底图分支生成有界网格");
                vm.ShowGrid=false;Check(vm.PreviewGridLines.Count==0,name+" 网格开关有效");vm.ShowGrid=true;
                vm.GridSize=.000001;Check(vm.PreviewGridLines.Count>0&&vm.PreviewGridLines.Count<=484,name+" 极细网格自适应预算");vm.GridSize=name=="6"?1:10;
                vm.UnderlayOpacity=.32;Capture(vm,"03-map"+name+"-optimized.png");
                if(name=="7")Capture(vm,"05-map7-1100x700.png",1100,700);
                var sparse=vm.PreviewNodes.Count(n=>n.ShowLabel);vm.SparseNodeLabels=false;var full=vm.PreviewNodes.Count(n=>n.ShowLabel);
                Check(full==count&&sparse<=full,name+" 可恢复全部文字且无节点删除");
                if(name=="7")Check(sparse<full/2,"密集图标签自动避让超过半数");
                vm.SparseNodeLabels=true;var initial=vm.PreviewNodes.Count(n=>n.ShowLabel);vm.AdjustCanvasZoom(120);vm.AdjustCanvasZoom(120);vm.AdjustCanvasZoom(120);
                Check(vm.PreviewNodes.Count(n=>n.ShowLabel)>=initial,name+" 放大恢复更多标签");vm.ResetCanvasViewCommand.Execute();
                var n=vm.Nodes[0];vm.SelectedNode=n;Check(vm.PreviewNodes.Single(p=>p.Id==n.Id).ShowLabel,name+" 选中点优先显示完整编号");
                Check(vm.PreviewNodes.Single(p=>p.Id==n.Id).Label==n.AgvSiteId,name+" 默认显示厂家编号但业务ID保持独立");
                vm.UseVendorSiteLabels=false;Check(vm.PreviewNodes.Single(p=>p.Id==n.Id).Label==n.Id,name+" 可切换业务编号");vm.UseVendorSiteLabels=true;
                vm.ShowNodeLayer=false;Check(vm.SelectedNode is null&&vm.FindNodeAtCanvas(10,10) is null,name+" 隐藏节点清除选择且不可命中");vm.ShowNodeLayer=true;
                vm.ShowDirectionArrows=false;Check(vm.PreviewEdges.All(e=>!e.ShowDirection),name+" 可单独隐藏方向箭头");vm.ShowDirectionArrows=true;
                vm.ShowScanLayer=false;vm.ShowSmapAnnotations=false;vm.ShowUnderlayLayer=false;vm.UnderlayOpacity=0;
                Check(vm.UnderlayOpacity==0&&Geometry(vm)==geometry,name+" 底图全透明与开关不改变拓扑或启用状态");
                Capture(vm,"04-map"+name+"-navigation-only.png");
                vm.ShowUnderlayLayer=true;vm.ShowScanLayer=true;vm.ShowSmapAnnotations=true;vm.UnderlayOpacity=.32;
                Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hash),name+" 原生源图字节未修改");
            }
            var beforeLayers=Geometry(vm);var total=vm.Nodes.Count;var navigation=vm.CurrentLayer!;
            vm.AddMapLayerCommand.Execute();var second=vm.CurrentLayer!;second.Name="示范分区";
            Check(vm.PreviewNodes.Count==total,"切换当前层默认保留其他层信息");
            vm.UpdateBatchNodeSelection([vm.Nodes[0]]);vm.MoveSelectionToLayerCommand.Execute();
            vm.OnlyCurrentLayer=true;Check(vm.PreviewNodes.Count==1&&vm.Nodes.Count==total,"仅看当前层只隔离显示不删除数据");
            vm.CurrentLayer=navigation;Check(vm.PreviewNodes.Count==total-1,"隔离模式切层切换可见对象");
            vm.CurrentLayer=second;second.Alpha=0;Check(vm.PreviewNodes.Count==0&&vm.SelectedNode is null,"透明对象不残留选择或可命中形状");
            Capture(vm,"06-layer-isolation.png");
            var oldCount=vm.Nodes.Count;vm.AddNodeCommand.Execute();Check(vm.Nodes.Count==oldCount&&vm.StatusMessage.Contains("取消新增"),"透明当前层禁止隐形新增");
            second=vm.CurrentLayer!;second.Alpha=1;second.IsAllowSelect=false;vm.AddNodeCommand.Execute();Check(vm.Nodes.Count==oldCount,"不允许选择的当前层禁止隐形新增");second=vm.CurrentLayer!;second.IsAllowSelect=true;
            second.IsLocked=true;vm.AddNodeCommand.Execute();Check(vm.Nodes.Count==oldCount,"锁定图层继续禁止新增");second=vm.CurrentLayer!;second.IsLocked=false;
            Check(second.Name=="示范分区","拒绝编辑后保留原当前层而非跳回默认层");
            vm.OnlyCurrentLayer=false;Check(vm.PreviewNodes.Count==total&&Geometry(vm)==beforeLayers,"退出隔离恢复全图且坐标路线编号保持");
            vm.ShowScanLayer=false;vm.ShowSmapAnnotations=true;vm.ShowEdgeLayer=false;vm.ShowNodeLabels=false;vm.UnderlayOpacity=.41;vm.OnlyCurrentLayer=true;vm.ShowGrid=false;
            var copy=Path.Combine(Evidence,"display-roundtrip.agvmap.json");Complete(vm.SaveLocalCopyAsync(copy));Complete(vm.OpenLocalFileAsync(copy));
            Check(!vm.ShowScanLayer&&vm.ShowSmapAnnotations&&!vm.ShowEdgeLayer&&!vm.ShowNodeLabels&&Math.Abs(vm.UnderlayOpacity-.41)<1e-9,"分类显示和透明度保存后恢复");
            Check(vm.OnlyCurrentLayer&&vm.CurrentLayer?.Name=="示范分区"&&!vm.ShowGrid&&vm.PreviewNodes.Count==1,"当前层隔离和网格保存后恢复");
            Check(vm.HasSmapBackgroundLayers&&vm.Nodes.Count==total,"另存重开保留底图通道与全部对象");
            var legacy=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(copy))!;legacy["project"]!.AsObject().Remove("smapScanPngPath");legacy["project"]!.AsObject().Remove("smapAnnotationPngPath");legacy["draft"]!["definition"]!["editorSettings"]!.AsObject().Remove("display");
            var legacyPath=Path.Combine(Evidence,"legacy-display.agvmap.json");File.WriteAllText(legacyPath,legacy.ToJsonString());Complete(vm.OpenLocalFileAsync(legacyPath));
            Check(!vm.HasSmapBackgroundLayers&&vm.UseCompositeUnderlay&&vm.UnderlayImage is not null,"旧草稿缺分项图片时退回完整合成底图");
            Check(vm.ShowNodeLabels&&vm.ShowEdgeLayer&&!vm.OnlyCurrentLayer&&vm.Nodes.Count==total,"旧草稿缺显示设置采用兼容默认且不丢对象");
            var workspaceTask=store.OpenFileAsync(copy);Complete(workspaceTask);var workspace=workspaceTask.Result;
            var renderStore=new LocalMapEditorStore(Path.Combine(Evidence,"cad-replacement"),new TestRenderer(workspace.Project.PreviewPngPath));
            var rendered=renderStore.RenderAsync(workspace,new(workspace.Project.MapId,"synthetic-replacement.dxf",[]));Complete(rendered);
            Check(rendered.Result.Project.SmapScanPngPath is null&&rendered.Result.Project.SmapAnnotationPngPath is null,"更换CAD底图清除旧扫描通道防止跨源叠图");
            var mixed=new Smap2DImportService().Read(File.ReadAllBytes("docs/examples/robokit-map-acceptance/mixed-types.smap"),"layers-test");
            Check(Enumerable.Range(0,mixed.ScanPixels.Length/4).Any(i=>mixed.ScanPixels[i*4+3]==255)&&Enumerable.Range(0,mixed.ScanPixels.Length/4).Any(i=>mixed.ScanPixels[i*4+3]==0),"扫描通道有内容且空白透明");
            Check(Enumerable.Range(0,mixed.AnnotationPixels.Length/4).Any(i=>mixed.AnnotationPixels[i*4+3]==255)&&Enumerable.Range(0,mixed.AnnotationPixels.Length/4).Any(i=>mixed.AnnotationPixels[i*4+3]==0),"厂家标注通道有内容且空白透明");
            Check(((RejectingTrafficProxy)proxy).Calls==0,"所有导入切层保存渲染均零后台及设备调用");vm.DeactivateEditor();
            File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=Results.Count,Failed=0,Results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+Results.Count);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());return 1;}
    }
    sealed class TestRenderer(string image):ICadUnderlayService
    {
        public Task<CadUnderlayRenderResult> RenderAsync(CadUnderlayRenderRequest request,CancellationToken cancellationToken)=>Task.FromResult(new CadUnderlayRenderResult(image,"",[],[],new(0,0,200,100,900,520),[]));
    }
}
