using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
internal static class Smap3DTest
{
 static readonly string Evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-smap3d-plane");
 static readonly string Fixtures=Path.GetFullPath("docs/examples/robokit-3d-plane-preview");
 static readonly List<object> Results=[];static readonly List<object> Performance=[];
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
 static void Complete(Task task){var limit=DateTime.UtcNow.AddSeconds(45);while(!task.IsCompleted&&DateTime.UtcNow<limit)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
 static byte[] Info(string text="{\"zipName\":\"SYNTHETIC-PREVIEW-NOT-DEVICE\"}")=>Encoding.UTF8.GetBytes(text);
 static byte[] Cloud()=>Encoding.UTF8.GetBytes("SYNTHETIC OPAQUE BYTES: NOT A REAL ROBOKIT 3D PROTOBUF OR POINT CLOUD. RETENTION TEST ONLY.");
 static byte[] Package(IEnumerable<(string Name,byte[] Bytes)> parts,CompressionLevel compression=CompressionLevel.Optimal,bool symlink=false)
 {
  using var bytes=new MemoryStream();using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))foreach(var part in parts){var e=zip.CreateEntry(part.Name,compression);if(symlink&&part.Name=="0.3dsmap")e.ExternalAttributes=unchecked((int)0xA1FF0000);using var stream=e.Open();stream.Write(part.Bytes);}return bytes.ToArray();
 }
 static (string Name,byte[] Bytes)[] Parts(byte[] plane)=>[("info.json",Info()),("0.smap",plane),("0.3dsmap",Cloud())];
 static void Reject(string name,byte[] bytes,Smap3DPackageReader.Limits? limits=null,string? expected=null)
 {
  try{new Smap3DPackageReader(limits).Read(bytes,"test-only");throw new Exception("接受了应拒绝的数据："+name);}
  catch(Exception ex)when(ex is InvalidDataException or JsonException or NotSupportedException or KeyNotFoundException or InvalidOperationException){Check(expected is null||ex.Message.Contains(expected),name+"："+ex.GetType().Name);}
 }
 [STAThread] static int Main()
 {
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());Directory.CreateDirectory(Evidence);Directory.CreateDirectory(Fixtures);
  try{Run();File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=Results.Count,Failed=0,Results},new JsonSerializerOptions{WriteIndented=true}));File.WriteAllText(Path.Combine(Evidence,"performance.json"),JsonSerializer.Serialize(new{Scope="Synthetic containers, local single-run observations; not device or maximum-map acceptance",Measurements=Performance},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+Results.Count);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());return 1;}
 }
 static void Run()
 {
  var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};foreach(var u in new[]{"pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml","pack://application:,,,/HandyControl;component/Themes/Theme.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Themes/Light.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/Typography.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml","pack://application:,,,/AgvDispatch.Wpf;component/Styles/Controls.xaml"})app.Resources.MergedDictionaries.Add(new(){Source=new Uri(u)});
  var plane=File.ReadAllBytes("docs/vendor/robokit/map-format-20261014/smap-master/maps/6.smap");var valid=Package(Parts(plane));
  var reader=new Smap3DPackageReader();var sw=Stopwatch.StartNew();var parsed=reader.Read(valid,"synthetic.smap");sw.Stop();
  Performance.Add(new{Name="Read official6 plane in synthetic ZIP",PackageBytes=valid.Length,ExpandedBytes=parsed.Entries.Sum(x=>x.ExpandedBytes),Milliseconds=sw.Elapsed.TotalMilliseconds});
  Check(parsed.ZipName=="SYNTHETIC-PREVIEW-NOT-DEVICE"&&parsed.Entries.Count==3,"规定三文件根目录结构读取");
  Check(!parsed.PointCloudParsed&&!parsed.DeviceValidated&&parsed.Entries.Single(x=>x.Name=="0.3dsmap").Sha256==Convert.ToHexString(SHA256.HashData(Cloud())),"三维数据仅完整性校验，明确不称已解析或实车有效");
  Check(parsed.Plane.Definition.Nodes.Count==6&&parsed.Plane.Definition.Edges.Count==14,"包内二维6点14路段数量一致");
  Check(parsed.PackageSha256==Convert.ToHexString(SHA256.HashData(valid))&&parsed.Plane.Sha256==Convert.ToHexString(SHA256.HashData(plane))&&parsed.PackageSha256!=parsed.Plane.Sha256,"原包与内层二维哈希分别记录");
  Check(parsed.Plane.Definition.Nodes.All(x=>x.Id=="BUS-"+x.AgvSiteId&&!x.Enabled)&&parsed.Plane.Definition.Edges.All(x=>!x.Enabled)&&parsed.Plane.Definition.Resources.Count==0,"厂家站点映射保留、导航停用、资源不生成");
  using(var original=JsonDocument.Parse(plane)){
   var points=original.RootElement.GetProperty("advancedPointList").EnumerateArray().ToArray();
   Check(points.All(p=>{var n=parsed.Plane.Definition.Nodes.Single(x=>x.AgvSiteId==p.GetProperty("instanceName").GetString());return Math.Abs(n.X+parsed.Plane.OffsetX-p.GetProperty("pos").GetProperty("x").GetDouble())<1e-8&&Math.Abs(n.Y+parsed.Plane.OffsetY-p.GetProperty("pos").GetProperty("y").GetDouble())<1e-8;}),"逐站坐标偏移可逆且厂家编号一致");
   var paths=original.RootElement.GetProperty("advancedCurveList").EnumerateArray().ToArray();
   Check(paths.Select((p,i)=>parsed.Plane.Definition.Edges[i].From=="BUS-"+p.GetProperty("startPos").GetProperty("instanceName").GetString()&&parsed.Plane.Definition.Edges[i].To=="BUS-"+p.GetProperty("endPos").GetProperty("instanceName").GetString()).All(x=>x),"逐路段起终点映射，重复路径名称不混淆方向");
  }
  foreach(var missing in new[]{"info.json","0.smap","0.3dsmap"})Reject("缺少"+missing,Package(Parts(plane).Where(x=>x.Name!=missing)));
  Reject("额外条目拒绝",Package(Parts(plane).Append(("extra.bin",new byte[]{1}))));
  Reject("重复文件拒绝",Package(Parts(plane).Append(("0.smap",plane))));
  foreach(var bad in new[]{"../0.smap","dir/0.smap","dir\\0.smap","/0.smap","C:/0.smap","0.SMAP"})Reject("非根目录或大小写不符 "+bad,Package(Parts(plane).Select(x=>x.Name=="0.smap"?(bad,x.Bytes):x)));
  Reject("目录项拒绝",Package(Parts(plane).Append(("folder/",Array.Empty<byte>()))));
  Reject("链接条目拒绝",Package(Parts(plane),symlink:true));
  foreach(var info in new[]{"{}","[]","{\"zipName\":123}","{\"zipName\":\"\"}","{\"zipName\":\"../bad\"}","{\"zipName\":\"a\",\"zipName\":\"b\"}","{\"zipName\":\"a\",\"extension\":1}","{"})Reject("无效info "+info,Package(Parts(plane).Select(x=>x.Name=="info.json"?(x.Name,Info(info)):x)));
  Reject("空三维条目拒绝",Package(Parts(plane).Select(x=>x.Name=="0.3dsmap"?(x.Name,Array.Empty<byte>()):x)));
  Reject("错误二维JSON拒绝",Package(Parts(Info("{}"))));
  var map3d=JsonNode.Parse(plane)!;map3d["header"]!["mapType"]="3D-Map";Reject("内层二维类型错误拒绝",Package(Parts(JsonSerializer.SerializeToUtf8Bytes(map3d))));
  Reject("损坏ZIP目录拒绝",valid[..(valid.Length-25)]);
  var crc=valid.ToArray();for(int i=0;i<crc.Length-20;i++)if(BitConverter.ToUInt32(crc,i)==0x02014b50){crc[i+16]^=1;break;}Reject("CRC损坏拒绝",crc,expected:"CRC");
  var noCompression=Package(Parts(plane),CompressionLevel.NoCompression);var damaged=noCompression.ToArray();var marker=Cloud();int index=damaged.AsSpan().IndexOf(marker);damaged[index]^=1;Reject("数据位翻转拒绝",damaged,expected:"CRC");
  Reject("压缩包大小预算拒绝",valid,new(PackageBytes:valid.Length-1));
  Reject("元信息大小预算拒绝",valid,new(InfoBytes:8));
  Reject("二维解压大小预算拒绝",valid,new(PlaneBytes:plane.Length-1));
  Reject("三维解压大小预算拒绝",valid,new(CloudBytes:Cloud().Length-1));
  Reject("总解压大小预算拒绝",valid,new(TotalBytes:plane.Length+Cloud().Length+Info().Length-1));
  Reject("压缩比预算拒绝",valid,new(CompressionRatio:1));
  try{reader.Read(valid,"cancelled",new CancellationToken(true));throw new Exception("取消无效");}catch(OperationCanceledException){Check(true,"已取消读取拒绝");}
  try{new Smap3DPackageReader(new(PackageBytes:65L*1024*1024));throw new Exception("预算可放大");}catch(ArgumentOutOfRangeException){Check(true,"测试预算不能放大生产上限");}
  TestUi("demo-6-3d-container",plane,app);
  TestUi("mixed-types-3d-container",File.ReadAllBytes("docs/examples/robokit-map-acceptance/mixed-types.smap"),app);
  var large=File.ReadAllBytes("docs/vendor/robokit/map-format-20261014/smap-master/maps/4.smap");var largeZip=Package(Parts(large));sw.Restart();var largeResult=reader.Read(largeZip,"synthetic-large.smap");sw.Stop();Performance.Add(new{Name="Larger official4 plane in synthetic ZIP",PackageBytes=largeZip.Length,ExpandedBytes=largeResult.Entries.Sum(x=>x.ExpandedBytes),Nodes=largeResult.Plane.Definition.Nodes.Count,Edges=largeResult.Plane.Definition.Edges.Count,Milliseconds=sw.Elapsed.TotalMilliseconds});Check(largeResult.Plane.Definition.Nodes.Count==133&&largeResult.Plane.Definition.Edges.Count==184,"较大二维部分133点184道路解析，非三维性能验收");
 }
 static void TestUi(string name,byte[] plane,System.Windows.Application app)
 {
  var bytes=Package(Parts(plane));var path=Path.Combine(Fixtures,name+".smap");File.WriteAllBytes(path,bytes);
  var watch=Stopwatch.StartNew();var store=new LocalMapEditorStore(Path.Combine(Evidence,"store-"+name));var task=store.OpenFileAsync(path);Complete(task);var ws=task.Result;watch.Stop();
  Performance.Add(new{Name="Local import "+name,Milliseconds=watch.Elapsed.TotalMilliseconds,PackageBytes=bytes.Length});
  Check(ws.Project.BackgroundFormat=="SMAP3D-2D"&&!ws.Project.IsActive&&ws.Published is null&&ws.Project.PublishedVersionId is null,"3D包导入仅本地草稿 "+name);
  var assets=Path.GetDirectoryName(ws.Project.BackgroundFilePath)!;
  Check(File.ReadAllBytes(ws.Project.BackgroundFilePath).SequenceEqual(bytes)&&File.ReadAllBytes(Path.Combine(assets,"0.smap")).SequenceEqual(plane),"原包和内层二维原字节留存 "+name);
  using(var review=JsonDocument.Parse(File.ReadAllText(Path.Combine(assets,"source-review.json"))))Check(review.RootElement.GetProperty("sha256").GetString()==Convert.ToHexString(SHA256.HashData(bytes)),"源摘要指向原包，不误用内层摘要 "+name);
  using(var review=JsonDocument.Parse(File.ReadAllText(Path.Combine(assets,"package-review.json"))))Check(!review.RootElement.GetProperty("pointCloudParsed").GetBoolean()&&!review.RootElement.GetProperty("deviceValidated").GetBoolean()&&!review.RootElement.GetProperty("writesEnabled").GetBoolean(),"报告禁止误称点云或现场通过 "+name);
  Check(new MapImportService().Import(new(path)).Nodes.Count==ws.Draft.Definition.Nodes.Count,"Auto导入同样支持包内平面 "+name);
  var proxy=System.Reflection.DispatchProxy.Create<ITrafficControlClient,RejectingTrafficProxy>();var vm=new MapManagementViewModel(proxy){LocalStore=store};Complete(vm.OpenLocalFileAsync(path));vm.SelectedNode=vm.Nodes.First();
  Check(vm.IsSmap3DPlanePreview&&vm.StatusMessage.Contains("二维部分")&&vm.UnderlayImage is not null&&vm.SelectedNode.AgvSiteId!="","正式界面标识二维部分及选中厂家站点属性 "+name);
  if(name.StartsWith("mixed"))Check(vm.ValidationIssues.Any(x=>x.Message.Contains("ArcPath")),"未核定ArcPath保持警告，不补造几何");
  ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var view=new MapManagementView{DataContext=vm,Width=1440,Height=900,Background=(Brush)app.FindResource("AppBackgroundBrush")};watch.Restart();view.Measure(new(1440,900));view.Arrange(new(0,0,1440,900));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();watch.Stop();Performance.Add(new{Name="XAML render "+name,Milliseconds=watch.Elapsed.TotalMilliseconds});
  var banner=(Border)view.FindName("Smap3DPlaneBanner");Check(banner.Visibility==Visibility.Visible&&banner.ActualHeight>0,"画布永久二维预览标识可见 "+name);
  var frame=ws.Project.CadViewBox!;Check(vm.PreviewNodes.All(p=>{var n=vm.Nodes.Single(x=>x.Id==p.Id);return Math.Abs(p.Left+7-n.X/frame.Width*900)<1e-6&&Math.Abs(p.Top+7-(1-n.Y/frame.Height)*520)<1e-6;}),"逐点背景像素对齐 "+name);
  var image=new RenderTargetBitmap(1440,900,96,96,PixelFormats.Pbgra32);image.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(Path.Combine(Evidence,name+".png")))encoder.Save(output);
  Complete(store.SaveAsync(ws));var reloaded=store.LoadAsync(ws.Project.MapId);Complete(reloaded);Check(reloaded.Result.Project.BackgroundFormat=="SMAP3D-2D","保存重载保留来源类型 "+name);
  var exported=Path.Combine(Evidence,name+".agvmap.json");Complete(store.ExportAsync(ws,exported));Complete(vm.OpenLocalFileAsync(exported));Check(vm.IsSmap3DPlanePreview&&vm.Nodes.Count==ws.Draft.Definition.Nodes.Count,"另存再打开仍明确二维部分 "+name);
  var oldIds=vm.Nodes.Select(x=>x.Id).ToArray();var bad=Path.Combine(Evidence,"invalid-"+name+".smap");File.WriteAllBytes(bad,Package(Parts(plane).Where(x=>x.Name!="0.3dsmap")));Complete(vm.OpenLocalFileAsync(bad));Check(vm.Nodes.Select(x=>x.Id).SequenceEqual(oldIds)&&vm.StatusMessage.Contains("失败")&&vm.IsSmap3DPlanePreview,"坏包不覆盖原编辑区或来源标识 "+name);
  Check(((RejectingTrafficProxy)proxy).Calls==0,"正式导入/重载/失败不调用后台或设备 "+name);vm.DeactivateEditor();
  var inspection=new Smap3DPackageReader().Read(bytes,path);
  File.WriteAllText(Path.Combine(Evidence,name+"-mapping.json"),JsonSerializer.Serialize(new{SyntheticContainer=true,Opaque3DIsNotValidDeviceFixture=true,SourcePlane=Path.GetFileName(name),inspection.PackageSha256,inspection.Entries,inspection.Plane.OffsetX,inspection.Plane.OffsetY,Nodes=inspection.Plane.Definition.Nodes,Edges=inspection.Plane.Definition.Edges,inspection.Plane.Warnings},new JsonSerializerOptions{WriteIndented=true}));
 }
}
