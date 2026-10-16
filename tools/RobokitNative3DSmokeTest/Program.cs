using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Controls;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.ViewModels;
using Prism.Mvvm;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Wpf.Views;
internal static class Program
{
 static readonly string Fixtures=Path.GetFullPath("docs/examples/robokit-native3d");
 static readonly string Evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-robokit-native3d");
 static readonly List<object> Results=[];static readonly List<object> Performance=[];
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
 static byte[] Raw(string name="synthetic-native.3dsmap")=>File.ReadAllBytes(Path.Combine(Fixtures,name));
 static byte[] Var(ulong value){var list=new List<byte>();do{var b=(byte)(value&127);value>>=7;list.Add((byte)(b|(value>0?128:0)));}while(value>0);return list.ToArray();}
 static byte[] Block(int field,byte[] value)=>Var((ulong)((field<<3)|2)).Concat(Var((ulong)value.Length)).Concat(value).ToArray();
 static byte[] Double(int field,double value)=>Var((ulong)((field<<3)|1)).Concat(BitConverter.GetBytes(value)).ToArray();
 static void Reject(byte[] bytes,string name,string? contains=null){try{new RobokitMap3DReader().Read(bytes,"test");throw new Exception("错误数据未拒绝："+name);}catch(Exception ex)when(ex is InvalidDataException or NotSupportedException or DecoderFallbackException){Check(contains is null||ex.Message.Contains(contains),name+" / "+ex.GetType().Name);}}
 static byte[] Package(byte[] original,byte[] cloud,bool movePlane=false)
 {
  using var input=new MemoryStream(original);using var zip=new ZipArchive(input,ZipArchiveMode.Read);using var output=new MemoryStream();using(var created=new ZipArchive(output,ZipArchiveMode.Create,true))foreach(var source in zip.Entries){using var stream=source.Open();using var buffer=new MemoryStream();stream.CopyTo(buffer);var data=source.Name=="0.3dsmap"?cloud:buffer.ToArray();
   if(movePlane&&source.Name=="0.smap"){var node=System.Text.Json.Nodes.JsonNode.Parse(data)!;node["header"]!["minPos"]!["x"]=500;node["header"]!["maxPos"]!["x"]=530;data=JsonSerializer.SerializeToUtf8Bytes(node);}
   var entry=created.CreateEntry(source.FullName);using var target=entry.Open();target.Write(data);
  }return output.ToArray();
 }
 static void Await(Task task){var deadline=DateTime.UtcNow.AddSeconds(40);while(!task.IsCompleted&&DateTime.UtcNow<deadline)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
 [STAThread] static int Main(){Directory.CreateDirectory(Evidence);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());try{Run();File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=Results.Count,Failed=0,Results,Performance},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+Results.Count);return 0;}catch(Exception ex){File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());Console.Error.WriteLine(ex);return 1;}}
 static void Run()
 {
  var reader=new RobokitMap3DReader();var raw=Raw();var watch=Stopwatch.StartNew();var cloud=reader.Read(raw,"raw");watch.Stop();Performance.Add(new{Name="Decode independent Google Protobuf fixture",Milliseconds=watch.Elapsed.TotalMilliseconds,Bytes=raw.Length,Points=cloud.Points.Count});
  Check(cloud.Points.Count==16975&&cloud.NativeMap?.MapName=="NATIVE-3D-SYNTHETIC","用户定义field3普通点云由独立编码器交叉验证");
  Check(cloud.Min.X==-19&&cloud.Max.X==-3&&cloud.Min.Y==0&&cloud.Max.Y==12&&cloud.Min.Z==0&&Math.Abs(cloud.Max.Z-3.6)<1e-9,"全部普通点边界正确，保持原始三维世界坐标");
  Check(cloud.Points[0].X==-19&&cloud.Points[0].Y==0&&cloud.Points[0].Z==0,"零值省略字段按proto3默认恢复");
  Check(cloud.NativeMap!.FeatureMapPresent&&cloud.NativeMap.FeaturePlaneEnvelopes==1,"特征地图仅存在/条目计数，不冒充普通点云");
  Check(!cloud.DeviceValidated&&!cloud.NativeMap.CoordinateAlignmentVerified&&cloud.NativeMap.SchemaSha256==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes("docs/vendor/robokit/map3d-definition-20261015/user-supplied-map.proto"))),"原文schema哈希关联，解码不等于实车或坐标核定");
  Check(cloud.Sha256==Convert.ToHexString(SHA256.HashData(raw)),"原生二进制原字节哈希保留");
  var zero=reader.Read(Raw("zero-default-point.3dsmap"),"zero");Check(zero.Points.Single() is {X:0,Y:0,Z:0},"合法空坐标子消息为零点");
  Check(reader.Read(Raw("unknown-version.3dsmap"),"version").NativeMap!.FormatVersion=="vendor-version-not-assumed","显示厂家格式版本，不套用二维版本白名单");
  Reject(Raw("feature-only.3dsmap"),"仅特征地图明确不可渲染","特征");Reject(Raw("wrong-type.3dsmap"),"错误map_type拒绝","header");Reject(Raw("nan-point.3dsmap"),"NaN原生点拒绝");Reject(Raw("out-of-bounds.3dsmap"),"超header边界拒绝","边界");Reject(Raw("bad-bounds.3dsmap"),"倒置边界拒绝");Reject(Raw("missing-header.3dsmap"),"缺少header拒绝");Reject(Raw("bad-resolution.3dsmap"),"零分辨率拒绝");
  Reject([],"空二进制拒绝");Reject([0],"字段0拒绝");Reject([0x80],"varint截断拒绝");Reject(Enumerable.Repeat((byte)255,10).ToArray(),"varint溢出拒绝");Reject([0x1A,0xFF,0x7F],"消息长度越界拒绝");Reject([0x18,0],"已知普通点字段wire type错误拒绝");Reject(Block(3,[9,0,0]),"double字段截断拒绝");Reject(raw[..(raw.Length-1)],"末消息截断拒绝");Reject(raw.Concat(Block(2,Block(2,[255]))).ToArray(),"header错误UTF8拒绝");Reject(raw.Concat(Var((ulong)((100<<3)|3))).ToArray(),"未支持group wire type拒绝");
  var unknown=raw.Concat(Var(100<<3)).Concat(new byte[]{1}).Concat(Var((101<<3)|1)).Concat(new byte[8]).Concat(Block(102,[1,2])).Concat(Var((103<<3)|5)).Concat(new byte[4]).ToArray();
  var extra=reader.Read(unknown,"unknown");Check(extra.Points.Count==cloud.Points.Count&&extra.NativeMap!.SkippedFields>=4,"四种未知普通wire字段按边界跳过并计数");
  var duplicate=raw.Concat(Block(2,Block(8,Encoding.UTF8.GetBytes("merged-version")))).ToArray();Check(reader.Read(duplicate,"merge").NativeMap!.FormatVersion=="merged-version","重复header子消息按proto合并/最后标量值");
  var one=Raw("zero-default-point.3dsmap").Concat(Block(3,Double(1,1).Concat(Double(1,3)).ToArray())).ToArray();Check(reader.Read(one,"scalar").Points.Last().X==3,"重复坐标标量最后值生效");
  try{reader.Read(raw,"cancel",new CancellationToken(true));throw new Exception("取消未拒绝");}catch(OperationCanceledException){Check(true,"原生解码响应取消");}
  var huge=Raw("feature-only.3dsmap").Concat(Enumerable.Range(0,1_000_001).SelectMany(_=>new byte[]{0x1A,0})).ToArray();Reject(huge,"超过百万普通点拒绝","100万");
  var packageBytes=File.ReadAllBytes(Path.Combine(Fixtures,"synthetic-native-3d.smap"));var source=new RobokitPointCloudSourceReader();var packaged=source.ReadPackage(packageBytes,"package");
  Check(packaged.Points.SequenceEqual(cloud.Points)&&packaged.Sha256==Convert.ToHexString(SHA256.HashData(packageBytes))&&packaged.NativeMap!.CloudSha256==cloud.Sha256,"原包SHA与内层SHA分别记录，解码点一致");
  Check(packaged.NativeMap!.PackageMapName==packaged.NativeMap.MapName&&packaged.NativeMap.AssociationSummary.Contains("6/6")&&packaged.NativeMap.AssociationSummary.Contains("14"),"同包地图名/XY范围/6站14路来源关联核验");
  Check(!packaged.NativeMap.CoordinateAlignmentVerified,"同包一致性不冒充坐标系/高度对齐");
  try{source.ReadPackage(Package(packageBytes,Raw("different-name.3dsmap")),"bad");throw new Exception("错地图名未拒绝");}catch(InvalidDataException ex){Check(ex.Message.Contains("名称"),"包内二维三维地图名不一致拒绝");}
  try{source.ReadPackage(Package(packageBytes,raw,true),"bad");throw new Exception("XY分离未拒绝");}catch(InvalidDataException ex){Check(ex.Message.Contains("重叠"),"包内二维三维XY边界分离拒绝");}
  var app=new AgvDispatch.Wpf.App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var view=new PointCloudView{Width=1200,Height=900};
  Await(view.LoadFileAsync(Path.Combine(Fixtures,"synthetic-native-3d.smap")));Check(view.Data?.NativeMap?.PackageMapName=="NATIVE-3D-SYNTHETIC"&&view.DisplayedCount==16975,"正式三维界面直接打开ZIP形式厂家包");
  Render(view,"native-package",1200,900);view.Orbit(50,10);view.ApplyHeightRange(1,2);Check(view.DisplayedPoints.All(p=>p.Z>=1&&p.Z<=2)&&view.DisplayedCount>0,"原生点云旋转和高度切片");Render(view,"native-height-slice",1200,900);
  Await(view.LoadFileAsync(Path.Combine(Fixtures,"synthetic-native.3dsmap")));Check(view.Data?.NativeMap is not null&&view.Data.NativeMap.PackageMapName is null,"独立3dsmap可读，不补造二维关联");Render(view,"native-raw-small",900,700);
  var current=view.Data;Await(view.LoadFileAsync(Path.Combine(Fixtures,"feature-only.3dsmap")));Check(ReferenceEquals(current,view.Data)&&view.Message.Contains("特征"),"不可渲染特征地图明确失败并保留原点云");view.Deactivate();
  var proxy=DispatchProxy.Create<ITrafficControlClient,NoBackend>();var vm=new MapManagementViewModel(proxy){LocalStore=new LocalMapEditorStore(Path.Combine(Evidence,"private-map-store"))};
  Await(vm.OpenLocalFileAsync(Path.Combine(Fixtures,"synthetic-native-3d.smap")));ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
  var mapView=new MapManagementView{DataContext=vm,Width=1440,Height=900};mapView.Measure(new(1440,900));mapView.Arrange(new(0,0,1440,900));mapView.UpdateLayout();
  var button=Descendants(mapView).OfType<Button>().Single(x=>Equals(x.Content,"查看本包三维点云"));button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  var opened=app.Windows.Cast<Window>().Single(x=>x.Content is PointCloudView);var openedView=(PointCloudView)opened.Content;var end=DateTime.UtcNow.AddSeconds(30);
  while(openedView.Data is null&&DateTime.UtcNow<end)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);
  Check(openedView.Data?.NativeMap?.PackageMapName=="NATIVE-3D-SYNTHETIC","正式二维预览按钮一键打开本包三维窗口并自动读取");
  opened.Close();Check(openedView.Data is null,"关闭正式窗口释放三维数据");Check(((NoBackend)(object)proxy).Calls==0,"二维导入到原生三维查看全程无后台或设备调用");vm.DeactivateEditor();
  File.WriteAllText(Path.Combine(Evidence,"native-metadata.json"),JsonSerializer.Serialize(new{packaged.NativeMap,packaged.Sha256,packaged.Min,packaged.Max,Count=packaged.Points.Count,packaged.DeviceValidated,DeviceWritesEnabled=false},new JsonSerializerOptions{WriteIndented=true}));
 }
 public class NoBackend:DispatchProxy{public int Calls;protected override object? Invoke(MethodInfo? targetMethod,object?[]? args){Calls++;throw new NotSupportedException("测试禁止后台调用");}}
 static IEnumerable<DependencyObject> Descendants(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var item in Descendants(child))yield return item;}}
 static void Render(PointCloudView view,string name,int width,int height){view.Width=width;view.Height=height;view.Measure(new(width,height));view.Arrange(new(0,0,width,height));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();var sw=Stopwatch.StartNew();var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);sw.Stop();Performance.Add(new{Name=name,Milliseconds=sw.Elapsed.TotalMilliseconds});var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(Evidence,name+".png"));encoder.Save(file);Check(view.Data is not null&&view.DisplayedCount>0,name+"实际三维渲染");}
}
