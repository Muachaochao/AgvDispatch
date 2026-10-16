using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Wpf.Views;
internal static class Program
{
 static readonly string Evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-pointcloud3d");static readonly List<object> Results=[];static readonly List<object> Performance=[];
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
 static void Await(Task task){var end=DateTime.UtcNow.AddSeconds(40);while(!task.IsCompleted&&DateTime.UtcNow<end)Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();}
 static byte[] Bytes(string text)=>Encoding.UTF8.GetBytes(text);
 static void Reject(string text,string name){try{new XyzPointCloudReader().Read(Bytes(text),"test");throw new Exception("错误输入未拒绝："+name);}catch(Exception ex)when(ex is InvalidDataException or DecoderFallbackException){Check(true,name);}}
 [STAThread] static int Main(){Directory.CreateDirectory(Evidence);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());try{Run();File.WriteAllText(Path.Combine(Evidence,"results.json"),JsonSerializer.Serialize(new{Passed=Results.Count,Failed=0,Results,Performance},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+Results.Count);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(Evidence,"failure.txt"),ex.ToString());return 1;}}
 static void Run()
 {
  var app=new AgvDispatch.Wpf.App();app.InitializeComponent();var reader=new XyzPointCloudReader();
  var input=Bytes("# test metres\n1 2 3\n-1, -2, -3\n0 0 0\n");var parsed=reader.Read(input,"test");
  Check(parsed.Points.Count==3&&parsed.Min.Z==-3&&parsed.Max.Z==3,"独立XYZ三列/逗号/注释/负高度解析");
  Check(parsed.Sha256==Convert.ToHexString(SHA256.HashData(input))&&!parsed.DeviceValidated,"原始文件SHA留存，不声称厂家格式核验");
  Check(reader.Read(Bytes("\uFEFF1\t2\t3\n"),"test").Points.Count==1,"UTF8 BOM与制表符支持");
  Reject("","空文件拒绝");Reject("# only comment","没有点拒绝");Reject("1 2","缺列拒绝");Reject("1 2 3 4","多列拒绝");Reject("1,,3","空列拒绝");Reject("1 NaN 3","NaN拒绝");Reject("Infinity 2 3","Infinity拒绝");Reject("1000001 0 0","超范围坐标拒绝");Reject("X Y Z","未声明表头拒绝");Reject(new string('1',4097),"超行长拒绝");
  try{reader.Read(new byte[]{0xFF,0xFF},"test");throw new Exception("UTF8错误未拒绝");}catch(DecoderFallbackException){Check(true,"错误UTF8拒绝");}
  try{reader.Read(input,"test",new CancellationToken(true));throw new Exception("取消未拒绝");}catch(OperationCanceledException){Check(true,"读取取消响应");}
  Reject(string.Concat(Enumerable.Repeat("0 0 0\n",XyzPointCloudReader.MaxPoints+1)),"超过100万点拒绝");
  Reject(string.Concat(Enumerable.Repeat("#\n",1_100_001)),"超过行数预算拒绝");
  try{reader.Read(new byte[XyzPointCloudReader.MaxBytes+1],"oversize");throw new Exception("超过大小预算未拒绝");}catch(InvalidDataException){Check(true,"超过64MiB字节预算拒绝");}
  var room=Path.GetFullPath("docs/examples/pointcloud-3d/synthetic-workshop.xyz");var sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(room)));
  var view=new PointCloudView{Width=1200,Height=850};var watch=Stopwatch.StartNew();Await(view.LoadFileAsync(room));watch.Stop();Performance.Add(new{Name="Parse+build workshop scene",Milliseconds=watch.Elapsed.TotalMilliseconds,Points=view.Data!.Points.Count});
  Check(view.Data.Points.Count>10000&&view.Data.Min.Z==0&&view.Data.Max.Z>3,"真正三维空间样例包含不同高度，非二维背景");
  Check(view.DisplayedCount==view.Data.Points.Count&&view.Message.Contains("总计"),"显示数量和源点数量明确");
  Layout(view,1200,850);var viewport=(Viewport3D)view.FindName("Viewport");
  Check(viewport.Children.Count==1&&((ModelVisual3D)viewport.Children[0]).Content is Model3DGroup,"正式Viewport3D几何场景创建");
  var group=(Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
  Check(group.Children.OfType<GeometryModel3D>().Count()>8,"高度颜色分组显示");
  Render(view,"workshop-perspective",1200,850);
  var initial=((PerspectiveCamera)viewport.Camera).Position;view.Orbit(45,15);Check(((PerspectiveCamera)viewport.Camera).Position!=initial,"旋转实际改变三维相机");Render(view,"workshop-rotated",1200,850);
  var distance=view.Distance;view.Zoom(.5);Check(view.Distance<distance,"缩放改变相机距离");view.Zoom(1e30);Check(view.Distance==20,"缩放远端上限");view.Zoom(1e-30);Check(view.Distance==.15,"缩放近端下限");view.Fit();
  view.Orbit(0,10000);Check(view.Pitch==89,"俯仰角限制避免翻转奇点");view.Orbit(0,-10000);Check(view.Pitch==-89,"俯仰下限");view.Fit();
  var cameraBefore=((PerspectiveCamera)viewport.Camera).Position;view.Pan(.1,.1);Check(((PerspectiveCamera)viewport.Camera).Position!=cameraBefore,"平移实际改变相机位置");view.Fit();
  Check(view.ApplyHeightRange(1,2)&&view.FilteredCount==view.Data.Points.Count(p=>p.Z>=1&&p.Z<=2)&&view.DisplayedPoints.All(p=>p.Z>=1&&p.Z<=2),"高度切片双闭区间与实际点一致");Render(view,"workshop-height-slice",1200,850);
  var count=view.DisplayedCount;Check(!view.ApplyHeightRange(2,1)&&view.DisplayedCount==count,"倒置高度拒绝且保留原显示");Check(!view.ApplyHeightRange(double.NaN,2),"NaN高度拒绝");
  Check(view.ApplyHeightRange(100,101)&&view.FilteredCount==0&&view.DisplayedCount==0,"空切片清空旧点");view.ApplyHeightRange(view.Data.Min.Z,view.Data.Max.Z);view.Fit();
  Render(view,"workshop-small",900,650);
  // A central single point produces a real ray hit at the projected scene origin.
  var single=Path.Combine(Evidence,"single.xyz");File.WriteAllText(single,"12 23 34\n");Await(view.LoadFileAsync(single));Layout(view,1200,850);var hit=view.Pick(new(viewport.ActualWidth/2,viewport.ActualHeight/2));Check(hit is {X:12,Y:23,Z:34},"真实三维射线选点返回原始坐标及高度");
  var plane=Path.Combine(Evidence,"flat.xyz");File.WriteAllText(plane,"0 0 5\n1 1 5\n");Await(view.LoadFileAsync(plane));Check(view.Data!.Min.Z==5&&view.DisplayedCount==2,"等高平面无除零或丢点");
  var large=Path.Combine(Evidence,"large.xyz");using(var writer=new StreamWriter(large)){writer.WriteLine("# synthetic 100001 points, metres");for(var i=0;i<100001;i++)writer.WriteLine(FormattableString.Invariant($"{i%1000*.01:F2} {i/1000*.02:F2} {i%37*.1:F2}"));}
  watch.Restart();Await(view.LoadFileAsync(large));watch.Stop();Performance.Add(new{Name="Parse+sample+build 100001 points",Milliseconds=watch.Elapsed.TotalMilliseconds,SourcePoints=view.Data!.Points.Count,Displayed=view.DisplayedCount});Check(view.Data.Points.Count==100001&&view.DisplayedCount<=PointCloudView.DisplayBudget&&view.DisplayedCount>0,"10万点加载保留全数据、显示不超2万点");
  Check(view.Message.Contains("采样")&&view.FilteredCount==100001,"采样限制在界面明确显示");
  var previous=view.Data;var invalid=Path.Combine(Evidence,"invalid.xyz");File.WriteAllText(invalid,"not data");Await(view.LoadFileAsync(invalid));Check(ReferenceEquals(view.Data,previous)&&view.Message.Contains("加载失败"),"坏文件保留原点云且明确失败");
  var unsupported=Path.Combine(Evidence,"unknown.3dsmap");File.WriteAllBytes(unsupported,input);Await(view.LoadFileAsync(unsupported));Check(ReferenceEquals(view.Data,previous)&&view.Message.Contains("加载失败"),"错误Protobuf输入拒绝且保留原点云");
  var first=view.LoadFileAsync(large);var second=view.LoadFileAsync(single);Await(Task.WhenAll(first,second));Check(view.Data!.Source==single&&view.Data.Points.Count==1,"换文件取消旧读取，迟到数据不覆盖新文件");
  var closing=view.LoadFileAsync(large);view.Deactivate();Await(closing);Check(view.Data is null&&view.DisplayedCount==0&&viewport.Children.Count==0,"关闭后取消读取并释放点云，不复活旧场景");
  Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(room)))==sourceHash,"查看/切片/选点不修改源文件");
  File.WriteAllText(Path.Combine(Evidence,"source.json"),JsonSerializer.Serialize(new{Source=room,Sha256=sourceHash,Synthetic=true,NativeRobokitDecoded=false,DeviceConnected=false,WritesEnabled=false},new JsonSerializerOptions{WriteIndented=true}));
 }
 static void Layout(PointCloudView view,int width,int height){view.Width=width;view.Height=height;view.Measure(new(width,height));view.Arrange(new(0,0,width,height));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);view.UpdateLayout();}
 static void Render(PointCloudView view,string name,int width,int height)
 {
  Layout(view,width,height);var viewport=(Viewport3D)view.FindName("Viewport");Check(viewport.ActualHeight>250&&viewport.ActualWidth>500,name+"视口可见");var watch=Stopwatch.StartNew();var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);watch.Stop();Performance.Add(new{Name=name+" render",Milliseconds=watch.Elapsed.TotalMilliseconds});var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(Path.Combine(Evidence,name+".png"));encoder.Save(output);
 }
}
