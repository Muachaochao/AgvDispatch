using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class NativeTest
{
    static string Evidence="";static List<object> Results=[];
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
    static void Complete(Task t){while(!t.IsCompleted)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);t.GetAwaiter().GetResult();}
    [STAThread]static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());Evidence=Path.GetFullPath("docs/commissioning/evidence/20261014-native-smap");Directory.CreateDirectory(Evidence);
        try
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};foreach(var u in new[]{"pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml","pack://application:,,,/HandyControl;component/Themes/Theme.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Themes/Light.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/Typography.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/Controls.xaml"})app.Resources.MergedDictionaries.Add(new(){Source=new Uri(u)});
            foreach(var p in Directory.GetFiles("docs/vendor/robokit/map-format-20261014/smap-master/maps","*.smap"))TestFile(p);
            var mixed="docs/examples/robokit-map-acceptance/mixed-types.smap";TestFile(mixed);
            var bytes=File.ReadAllBytes(mixed);var node=JsonNode.Parse(bytes)!;
            void Reject(string name,Action<JsonNode> mutate){var v=node.DeepClone();mutate(v);try{new Smap2DImportService().Read(JsonSerializer.SerializeToUtf8Bytes(v),"test-only");throw new Exception("未拒绝："+name);}catch(Exception ex)when(ex is InvalidDataException or NotSupportedException or KeyNotFoundException or InvalidOperationException){Check(true,name);}}
            Reject("未知格式版本拒绝",n=>n["header"]!["version"]="9.0.0");Reject("3D类型拒绝",n=>n["header"]!["mapType"]="3D-Map");Reject("分辨率0拒绝",n=>n["header"]!["resolution"]=0);Reject("逆序边界拒绝",n=>n["header"]!["maxPos"]!["x"]=-10);
            Reject("重复站点拒绝",n=>n["advancedPointList"]![1]!["instanceName"]="SITE1");Reject("仅大小写不同站点拒绝",n=>n["advancedPointList"]![1]!["instanceName"]="site1");Reject("端点未找到拒绝",n=>n["advancedCurveList"]![0]!["startPos"]!["instanceName"]="MISSING");Reject("端点坐标不一致拒绝",n=>n["advancedCurveList"]![0]!["startPos"]!["pos"]!["x"]=90);Reject("字符串坐标拒绝",n=>n["advancedPointList"]![0]!["pos"]!["x"]="2");Reject("数组类型错误拒绝",n=>n["normalPosList"]="INVALID");
            try{new Smap2DImportService().Read(new byte[]{0x50,0x4B,3,4},"zip");throw new Exception("ZIP未拒绝");}catch(NotSupportedException){Check(true,"ZIP文件拒绝");}
            var unknown=node.DeepClone();unknown["advancedPointList"]![0]!["className"]="FUTURE-TYPE";var parsed=new Smap2DImportService().Read(JsonSerializer.SerializeToUtf8Bytes(unknown),"test");Check(parsed.Warnings.Any(w=>w.Contains("未知高级点"))&&parsed.Definition.Nodes.All(n=>!n.Enabled),"未知点只停用显示并保留警告");
            var store=new LocalMapEditorStore(Path.Combine(Evidence,"failure-store"));var proxy=System.Reflection.DispatchProxy.Create<ITrafficControlClient,RejectingTrafficProxy>();var vm=new MapManagementViewModel(proxy){LocalStore=store};Complete(vm.OpenLocalFileAsync(mixed));var before=vm.Nodes.Select(n=>n.Id).ToArray();var bad=Path.Combine(Evidence,"invalid.smap");File.WriteAllText(bad,"{}");Complete(vm.OpenLocalFileAsync(bad));Check(vm.Nodes.Select(n=>n.Id).SequenceEqual(before)&&vm.StatusMessage.Contains("失败"),"错误导入保留当前编辑地图");Check(((RejectingTrafficProxy)proxy).Calls==0,"失败/成功均无后台或现场调用");vm.DeactivateEditor();
            File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=Results.Count,Failed=0,Results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+Results.Count);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());return 1;}
    }
    static void TestFile(string path)
    {
        var name=Path.GetFileNameWithoutExtension(path);var bytes=File.ReadAllBytes(path);using var doc=JsonDocument.Parse(bytes);var expected=doc.RootElement.TryGetProperty("advancedPointList",out var points)?points.GetArrayLength():0;
        var store=new LocalMapEditorStore(Path.Combine(Evidence,"store-"+name));var task=store.OpenFileAsync(path);Complete(task);var workspace=task.Result;
        Check(workspace.Draft.Definition.Nodes.Count==expected,name+" 原生Smap高级点数量一致");Check(workspace.Project.BackgroundFormat=="SMAP" && workspace.Published is null && !workspace.Project.IsActive,name+" 只产生本地草稿");
        Check(File.ReadAllBytes(workspace.Project.BackgroundFilePath).SequenceEqual(bytes),name+" 原图字节完整留存");Check(File.Exists(Path.Combine(Path.GetDirectoryName(workspace.Project.BackgroundFilePath)!,"source-review.json")),name+" 关联哈希和转换说明保存");Check(workspace.Draft.Definition.Nodes.All(n=>!n.Enabled&&n.AgvSiteId is not null)&&workspace.Draft.Definition.Edges.All(e=>!e.Enabled),name+" 站点映射独立且导航停用");
        var importer=new MapImportService().Import(new(path));Check(importer.Nodes.Count==expected,name+" Auto格式导入识别smap");
        var copy=Path.Combine(Evidence,name+".agvmap.json");Complete(store.ExportAsync(workspace,copy));var reopened=store.OpenFileAsync(copy);Complete(reopened);Check(reopened.Result.Project.MapId!=workspace.Project.MapId && reopened.Result.Draft.Definition.Nodes.Count==expected,name+" 本地保存再打开保留图且重置活动身份");
        var proxy=System.Reflection.DispatchProxy.Create<ITrafficControlClient,RejectingTrafficProxy>();var vm=new MapManagementViewModel(proxy){LocalStore=store};Complete(vm.OpenLocalFileAsync(path));Check(vm.Nodes.Count==expected&&vm.UnderlayImage is not null&&vm.StatusMessage.Contains("原生导入完成"),name+" 正式ViewModel直接打开smap并显示提示");
        if(name=="mixed-types")Check(vm.ValidationIssues.Any(x=>x.Message.Contains("ArcPath")),"ArcPath未核定在界面校验列表明确提示");
        if(name is "6" or "mixed-types")
        {
            ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var view=new MapManagementView{DataContext=vm,Width=1440,Height=900,Background=(Brush)System.Windows.Application.Current.FindResource("AppBackgroundBrush")};view.Measure(new Size(1440,900));view.Arrange(new Rect(0,0,1440,900));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();
            var frame=workspace.Project.CadViewBox!;foreach(var p in vm.PreviewNodes){var n=vm.Nodes.Single(n=>n.Id==p.Id);if(Math.Abs(p.Left+7-n.X/frame.Width*900)>1e-6||Math.Abs(p.Top+7-(1-n.Y/frame.Height)*520)>1e-6)throw new Exception("原生像素对齐失败");}Check(true,name+" 原生站点像素与背景对齐");
            var b=new RenderTargetBitmap(1440,900,96,96,PixelFormats.Pbgra32);b.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(b));using var stream=File.Create(Path.Combine(Evidence,"native-"+name+".png"));encoder.Save(stream);
        }
        Check(((RejectingTrafficProxy)proxy).Calls==0,name+" 正式导入无后台调用");vm.DeactivateEditor();
    }
}
