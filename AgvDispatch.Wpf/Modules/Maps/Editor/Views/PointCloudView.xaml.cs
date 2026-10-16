using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Maps;
using Microsoft.Win32;

namespace AgvDispatch.Wpf.Views;

public partial class PointCloudView:UserControl
{
    public const int DisplayBudget=20_000;
    public PointCloudData? Data {get;private set;}
    public int DisplayedCount {get;private set;}
    public int FilteredCount {get;private set;}
    public double Yaw {get;private set;}=-55;
    public double Pitch {get;private set;}=28;
    public double Distance {get;private set;}=2.8;
    public CloudPoint? SelectedPoint {get;private set;}
    public string Message=>StatusText.Text;
    public IReadOnlyList<CloudPoint> DisplayedPoints=>_displayed.AsReadOnly();
    private readonly List<CloudPoint> _displayed=[];
    private readonly Dictionary<MeshGeometry3D,int[]> _meshPoints=[];
    private long _generation;
    private CancellationTokenSource? _request;
    private bool _active=true;
    private Point? _dragStart,_last;
    private bool _rightDrag,_moved;
    private Point3D _target=new();
    private CloudPoint _center=new(0,0,0);
    private double _scale=1;
    public PointCloudView()
    {
        InitializeComponent();UpdateCamera();Loaded+=(_,_)=>_active=true;Unloaded+=(_,_)=>Deactivate();
    }
    public void Deactivate(){_active=false;_generation++;_request?.Cancel();Data=null;_displayed.Clear();_meshPoints.Clear();Viewport.Children.Clear();DisplayedCount=FilteredCount=0;SelectedPoint=null;Viewport.ReleaseMouseCapture();}
    public async Task<bool> LoadFileAsync(string path)
    {
        if(!_active)return false;
        _request?.Cancel();var generation=++_generation;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));_request=timeout;
        StatusText.Text="正在读取点云并核对输入格式…";
        try
        {
            var data=await new RobokitPointCloudSourceReader().ReadFileAsync(path,timeout.Token).WaitAsync(timeout.Token);
            if(!_active||generation!=_generation)return false;
            Data=data;_center=new((data.Min.X+data.Max.X)/2,(data.Min.Y+data.Max.Y)/2,(data.Min.Z+data.Max.Z)/2);
            _scale=Math.Max(1e-6,Math.Max(data.Max.X-data.Min.X,Math.Max(data.Max.Y-data.Min.Y,data.Max.Z-data.Min.Z)));
            var format=data.NativeMap is null?"XYZ(m)声明":"Robokit Message_Map3D普通点云";
            SourceText.Text=$"{System.IO.Path.GetFileName(path)} · {data.Points.Count:N0}点 · {format} · SHA256 {data.Sha256}\nX [{data.Min.X:G8}, {data.Max.X:G8}] · Y [{data.Min.Y:G8}, {data.Max.Y:G8}] · Z [{data.Min.Z:G8}, {data.Max.Z:G8}]";
            if(data.NativeMap is { } native)SourceText.Text+=$"\n地图 {native.MapName} · 格式 {native.FormatVersion} · 分辨率 {native.Resolution:G8}m · 特征地图存在={native.FeatureMapPresent}（{native.FeaturePlaneEnvelopes}个平面消息仅计数，特征未渲染）\n{native.AssociationSummary}";
            MinHeightBox.Text=data.Min.Z.ToString("G17",CultureInfo.InvariantCulture);MaxHeightBox.Text=data.Max.Z.ToString("G17",CultureInfo.InvariantCulture);
            Render(data.Min.Z,data.Max.Z);Fit();return true;
        }
        catch(Exception ex){if(_active&&generation==_generation)StatusText.Text="加载失败，保留原点云（如有）："+ex.Message;return false;}
        finally{if(ReferenceEquals(_request,timeout))_request=null;}
    }
    private async void Open_OnClick(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog{Filter="点云文件 (*.smap;*.3dsmap;*.xyz)|*.smap;*.3dsmap;*.xyz|Robokit 3D地图包 (*.smap)|*.smap|Robokit三维二进制 (*.3dsmap)|*.3dsmap|XYZ文本 (*.xyz)|*.xyz",CheckFileExists=true,Title="打开原生Robokit普通点云或XYZ（只读）"};
        if(picker.ShowDialog()==true)await LoadFileAsync(picker.FileName);
    }
    public bool ApplyHeightRange(double minimum,double maximum)
    {
        if(Data is null)return false;
        if(!double.IsFinite(minimum)||!double.IsFinite(maximum)||minimum>maximum||Math.Abs(minimum)>1e6||Math.Abs(maximum)>1e6){StatusText.Text="高度范围无效，保留原显示。";return false;}
        Render(minimum,maximum);return true;
    }
    private void Slice_OnClick(object sender,RoutedEventArgs e)
    {
        if(!double.TryParse(MinHeightBox.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var min)||!double.TryParse(MaxHeightBox.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var max)){StatusText.Text="高度需要填写有限数字（米）。";return;}
        ApplyHeightRange(min,max);
    }
    private void All_OnClick(object sender,RoutedEventArgs e){if(Data is not { } data)return;MinHeightBox.Text=data.Min.Z.ToString("G17",CultureInfo.InvariantCulture);MaxHeightBox.Text=data.Max.Z.ToString("G17",CultureInfo.InvariantCulture);Render(data.Min.Z,data.Max.Z);}
    private void Fit_OnClick(object sender,RoutedEventArgs e)=>Fit();
    private void Top_OnClick(object sender,RoutedEventArgs e){Yaw=-90;Pitch=89;_target=new();UpdateCamera();}
    public void Fit(){Yaw=-55;Pitch=28;Distance=2.8;_target=new();UpdateCamera();}
    public void Orbit(double yaw,double pitch){if(!double.IsFinite(yaw)||!double.IsFinite(pitch))return;Yaw=(Yaw+yaw)%360;Pitch=Math.Clamp(Pitch+pitch,-89,89);UpdateCamera();}
    public void Zoom(double factor){if(!double.IsFinite(factor)||factor<=0)return;Distance=Math.Clamp(Distance*factor,.15,20);UpdateCamera();}
    public void Pan(double horizontal,double vertical)
    {
        if(!double.IsFinite(horizontal)||!double.IsFinite(vertical))return;
        horizontal=Math.Clamp(horizontal,-1,1);vertical=Math.Clamp(vertical,-1,1);
        var camera=(PerspectiveCamera)Viewport.Camera;var forward=camera.LookDirection;forward.Normalize();var right=Vector3D.CrossProduct(forward,new(0,0,1));right.Normalize();var up=Vector3D.CrossProduct(right,forward);up.Normalize();
        _target+=right*(horizontal*Distance)+up*(vertical*Distance);_target=new(Math.Clamp(_target.X,-10,10),Math.Clamp(_target.Y,-10,10),Math.Clamp(_target.Z,-10,10));UpdateCamera();
    }
    private void UpdateCamera()
    {
        var yaw=Yaw*Math.PI/180;var pitch=Pitch*Math.PI/180;
        var offset=new Vector3D(Math.Cos(yaw)*Math.Cos(pitch),Math.Sin(yaw)*Math.Cos(pitch),Math.Sin(pitch))*Distance;
        Viewport.Camera=new PerspectiveCamera(_target+offset,-offset,new(0,0,1),45){NearPlaneDistance=.001,FarPlaneDistance=100};
    }
    private Point3D Normalize(CloudPoint p)=>new((p.X-_center.X)/_scale,(p.Y-_center.Y)/_scale,(p.Z-_center.Z)/_scale);
    private void Render(double min,double max)
    {
        if(Data is not { } data)return;
        _displayed.Clear();_meshPoints.Clear();SelectedPoint=null;PointText.Text="未选择点；选点只对当前显示的采样点有效。";
        Viewport.Children.Clear();
        FilteredCount=data.Points.Count(p=>p.Z>=min&&p.Z<=max);
        var stride=Math.Max(1,(int)Math.Ceiling(FilteredCount/(double)DisplayBudget));var matched=0;
        foreach(var point in data.Points){if(point.Z<min||point.Z>max)continue;if(matched++%stride==0)_displayed.Add(point);}
        DisplayedCount=_displayed.Count;
        var meshes=Enumerable.Range(0,16).Select(_=>new MeshGeometry3D()).ToArray();var ids=Enumerable.Range(0,16).Select(_=>new List<int>()).ToArray();
        const double radius=.0035;var height=Math.Max(1e-9,data.Max.Z-data.Min.Z);
        for(var i=0;i<_displayed.Count;i++)
        {
            var point=_displayed[i];var bin=Math.Clamp((int)((point.Z-data.Min.Z)/height*15),0,15);var mesh=meshes[bin];var center=Normalize(point);var index=mesh.Positions.Count;
            mesh.Positions.Add(center+new Vector3D(radius,0,-radius*.5));mesh.Positions.Add(center+new Vector3D(-radius,0,-radius*.5));mesh.Positions.Add(center+new Vector3D(0,radius,radius*.5));mesh.Positions.Add(center+new Vector3D(0,-radius,radius*.5));
            foreach(var t in new[]{0,1,2,0,3,1,0,2,3,1,3,2})mesh.TriangleIndices.Add(index+t);ids[bin].Add(i);
        }
        var group=new Model3DGroup();group.Children.Add(new AmbientLight(Colors.White));
        for(var bin=0;bin<16;bin++)
        {
            if(ids[bin].Count==0)continue;
            var mesh=meshes[bin];mesh.Freeze();_meshPoints.Add(mesh,ids[bin].ToArray());
            var t=bin/15d;var color=t<.5?Mix(Color.FromRgb(49,121,237),Color.FromRgb(52,199,174),t*2):Mix(Color.FromRgb(52,199,174),Color.FromRgb(248,203,80),(t-.5)*2);
            var material=new DiffuseMaterial(new SolidColorBrush(color));material.Freeze();var geometry=new GeometryModel3D(mesh,material){BackMaterial=material};geometry.Freeze();group.Children.Add(geometry);
        }
        group.Freeze();Viewport.Children.Add(new ModelVisual3D{Content=group});
        StatusText.Text=$"显示 {DisplayedCount:N0} / 切片 {FilteredCount:N0} / 总计 {data.Points.Count:N0} 点 · 显示上限{DisplayBudget:N0}，按文件顺序等步长采样 · Z [{min:G8}, {max:G8}] m · 颜色按原文件高度范围";
    }
    private static Color Mix(Color a,Color b,double t)=>Color.FromRgb((byte)(a.R+(b.R-a.R)*t),(byte)(a.G+(b.G-a.G)*t),(byte)(a.B+(b.B-a.B)*t));
    public CloudPoint? Pick(Point position)
    {
        SelectedPoint=null;
        VisualTreeHelper.HitTest(Viewport,null,result=>
        {
            if(result is RayMeshGeometry3DHitTestResult hit&&_meshPoints.TryGetValue(hit.MeshHit,out var map))
            {
                var local=hit.VertexIndex1/4;
                if(local>=0&&local<map.Length){SelectedPoint=_displayed[map[local]];PointText.Text=$"原始点坐标（输入米制）：X={SelectedPoint.X:G10}, Y={SelectedPoint.Y:G10}, Z={SelectedPoint.Z:G10} · 实车关联未核定";return HitTestResultBehavior.Stop;}
            }
            return HitTestResultBehavior.Continue;
        },new PointHitTestParameters(position));
        if(SelectedPoint is null)PointText.Text="该位置没有命中当前显示点；可放大后再选。";
        return SelectedPoint;
    }
    private void Viewport_OnMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton is not (MouseButton.Left or MouseButton.Right))return;_last=_dragStart=e.GetPosition(Viewport);_rightDrag=e.ChangedButton==MouseButton.Right;_moved=false;Viewport.CaptureMouse();e.Handled=true;
    }
    private void Viewport_OnMouseMove(object sender,MouseEventArgs e)
    {
        if(_last is not { } last||!Viewport.IsMouseCaptured)return;var point=e.GetPosition(Viewport);var delta=point-last;
        if(_dragStart is { } start&&(point-start).Length>3)_moved=true;
        if(_rightDrag)Pan(-delta.X/Math.Max(1,Viewport.ActualWidth),delta.Y/Math.Max(1,Viewport.ActualHeight));else Orbit(delta.X*.4,delta.Y*.4);_last=point;
    }
    private void Viewport_OnMouseUp(object sender,MouseButtonEventArgs e)
    {
        if(_last is null)return;var click=!_moved&&!_rightDrag;_last=null;_dragStart=null;Viewport.ReleaseMouseCapture();if(click)Pick(e.GetPosition(Viewport));e.Handled=true;
    }
    private void Viewport_OnMouseWheel(object sender,MouseWheelEventArgs e){Zoom(e.Delta>0?.85:1/.85);e.Handled=true;}
    private void Viewport_OnLostCapture(object sender,MouseEventArgs e){_last=null;_dragStart=null;}
}
