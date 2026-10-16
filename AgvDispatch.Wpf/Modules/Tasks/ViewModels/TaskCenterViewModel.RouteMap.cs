using System.Windows;
using System.Windows.Media;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;
public sealed record TaskCreationMapRoad(Geometry Geometry);
public sealed record TaskCreationMapDot(double Left,double Top,string Tooltip);
public sealed record TaskCreationRouteMapStroke(Geometry Geometry,Geometry Arrow,string Color,string Tooltip,ManualTaskRouteSegment Segment);

public sealed partial class TaskCenterViewModel
{
    private ManualTaskRouteMap? _creationMap;
    private string _creationMapVehicle="",_creationMapStation="",_creationMapOrigin="",_creationMapRevision="";
    private Dictionary<string,ManualTaskRouteMapEdge> _creationMapEdges=new(StringComparer.Ordinal);
    private Dictionary<string,ManualTaskRouteMapNode> _creationMapNodes=new(StringComparer.Ordinal);
    private ManualTaskRouteSegment? _selectedCreationRouteSegment;
    private string _creationMapMessage="查询当前四段路线后显示同批后台导航快照。";
    private double _creationMapScale=1,_creationMapMidX,_creationMapMidY,_creationMapZoom=1,_creationMapPanX,_creationMapPanY;
    private bool _creationMapOpen;
    public bool CreationRouteMapOpen {get=>_creationMapOpen;set=>SetProperty(ref _creationMapOpen,value);}
    public string CreationRouteMapMessage {get=>_creationMapMessage;private set=>SetProperty(ref _creationMapMessage,value);}
    public double CreationMapZoom {get=>_creationMapZoom;private set=>SetProperty(ref _creationMapZoom,value);}
    public double CreationMapPanX {get=>_creationMapPanX;private set=>SetProperty(ref _creationMapPanX,value);}
    public double CreationMapPanY {get=>_creationMapPanY;private set=>SetProperty(ref _creationMapPanY,value);}
    public IReadOnlyList<TaskCreationMapRoad> CreationMapRoads {get;private set;}=[];
    public IReadOnlyList<TaskCreationMapDot> CreationMapDots {get;private set;}=[];
    public IReadOnlyList<TaskCreationRouteMapStroke> CreationMapRoutes {get;private set;}=[];
    public Geometry CreationMapSelectedGeometry {get;private set;}=Geometry.Empty;
    public Geometry CreationMapSelectedArrow {get;private set;}=Geometry.Empty;
    public ManualTaskRouteSegment? SelectedCreationRouteSegment {get=>_selectedCreationRouteSegment;set{if(ReferenceEquals(value,_selectedCreationRouteSegment))return;LocateCreationRouteSegment(value);}}
    public DelegateCommand FitCreationRouteMapCommand {get;private set;}=null!;
    public DelegateCommand LocateFailedCreationRouteCommand {get;private set;}=null!;
    public DelegateCommand ZoomInCreationRouteMapCommand {get;private set;}=null!;
    public DelegateCommand ZoomOutCreationRouteMapCommand {get;private set;}=null!;
    private void InitializeCreationRouteMap()
    {
        FitCreationRouteMapCommand=new(()=>{CreationMapZoom=1;CreationMapPanX=0;CreationMapPanY=0;});
        LocateFailedCreationRouteCommand=new(()=>LocateCreationRouteSegment(CreationRouteSegments.First(s=>s.Status=="不符")),()=>_creationMap is not null&&CreationRouteSegments.Any(s=>s.Status=="不符"));
        ZoomInCreationRouteMapCommand=new(()=>ZoomCreationMap(1.5));ZoomOutCreationRouteMapCommand=new(()=>ZoomCreationMap(1/1.5));
    }
    private void ZoomCreationMap(double factor){var next=Math.Clamp(CreationMapZoom*factor,.5,64);var ratio=next/CreationMapZoom;CreationMapPanX*=ratio;CreationMapPanY*=ratio;CreationMapZoom=next;}
    private void ClearCreationRouteMap(string reason)
    {
        _creationMap=null;_creationMapVehicle=_creationMapStation=_creationMapOrigin=_creationMapRevision="";_creationMapEdges.Clear();_creationMapNodes.Clear();_selectedCreationRouteSegment=null;
        CreationMapRoads=[];CreationMapDots=[];CreationMapRoutes=[];CreationMapSelectedGeometry=Geometry.Empty;CreationMapSelectedArrow=Geometry.Empty;
        CreationMapZoom=1;CreationMapPanX=0;CreationMapPanY=0;CreationRouteMapOpen=false;CreationRouteMapMessage=reason;
        RaiseCreationMapChanges();LocateFailedCreationRouteCommand?.RaiseCanExecuteChanged();
    }
    private void RaiseCreationMapChanges()
    {
        foreach(var name in new[]{nameof(CreationMapRoads),nameof(CreationMapDots),nameof(CreationMapRoutes),nameof(CreationMapSelectedGeometry),nameof(CreationMapSelectedArrow),nameof(SelectedCreationRouteSegment)})RaisePropertyChanged(name);
    }
    private string CreationMapContext=>_creationMap is not {} map?"无地图上下文。":$"同批后台快照 · {map.MapId}/{map.VersionId} · 本地米制；底图节点{map.Nodes.Count}/{map.TotalNodes}、道路{map.Edges.Count}/{map.TotalEdges}。仅当前查询快照，不编辑运行图、不授予路权。";
    private Point CreationMapPoint(double x,double y)=>new(450+(x-_creationMapMidX)*_creationMapScale,250-(y-_creationMapMidY)*_creationMapScale);
    private Geometry CreationMapGeometry(IReadOnlyList<MapPoint2D> points)
    {
        var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(CreationMapPoint(points[0].X,points[0].Y),false,false);context.PolyLineTo(points.Skip(1).Select(p=>CreationMapPoint(p.X,p.Y)).ToArray(),true,false);}geometry.Freeze();return geometry;
    }
    private void LoadCreationRouteMap(ManualTaskRoutePreview preview)
    {
        ClearCreationRouteMap("地图不可用："+preview.MapIssue);
        if(preview.Map is not {} map)return;
        try
        {
            static bool Id(string? id)=>!string.IsNullOrWhiteSpace(id)&&id.Length<=256;
            static bool Coordinate(double n)=>double.IsFinite(n)&&Math.Abs(n)<=1e6;
            if(map.MapId!=preview.MapId||map.VersionId!=preview.MapVersionId||map.ObservationId!=preview.ObservationId||!Guid.TryParseExact(map.ObservationId,"N",out _)||!Guid.TryParseExact(map.RuntimeId,"N",out _)||map.Unit!="m"||map.Nodes.Count is <1 or >2048||map.Edges.Count>1000||map.TotalNodes<map.Nodes.Count||map.TotalEdges<map.Edges.Count||preview.Segments.Any(s=>s.Status=="已失效"))throw new InvalidOperationException("地图身份、批次、数量、单位或时效无效。");
            var nodes=new Dictionary<string,ManualTaskRouteMapNode>(StringComparer.Ordinal);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var node in map.Nodes)
                if(!Id(node.Id)||!names.Add(node.Id)||node.VendorId is null||node.VendorId.Length>256||!Coordinate(node.X)||!Coordinate(node.Y)||!nodes.TryAdd(node.Id,node))throw new InvalidOperationException("地图节点无效或编号重复。");
            var edges=new Dictionary<string,ManualTaskRouteMapEdge>(StringComparer.Ordinal);names.Clear();
            foreach(var edge in map.Edges)
            {
                if(!Id(edge.Id)||!names.Add(edge.Id)||!nodes.TryGetValue(edge.From,out var a)||!nodes.TryGetValue(edge.To,out var b)||edge.Points.Count is <2 or >33||edge.Points.Any(p=>!Coordinate(p.X)||!Coordinate(p.Y))||!Enum.TryParse<MapEdgeGeometryKind>(edge.Kind,out var kind)||!Enum.IsDefined(kind)||(kind==MapEdgeGeometryKind.Line?edge.Points.Count!=2:edge.Points.Count!=33)||Math.Abs(edge.Points[0].X-a.X)>1e-6||Math.Abs(edge.Points[0].Y-a.Y)>1e-6||Math.Abs(edge.Points[^1].X-b.X)>1e-6||Math.Abs(edge.Points[^1].Y-b.Y)>1e-6||!edges.TryAdd(edge.Id,edge))throw new InvalidOperationException("地图道路几何、端点或编号无效。");
            }
            _creationMap=map;_creationMapNodes=nodes;_creationMapEdges=edges;_creationMapVehicle=preview.VehicleId;_creationMapStation=preview.StationId;_creationMapOrigin=preview.CurrentSite;_creationMapRevision=preview.ParameterRevision;
            var points=map.Nodes.Select(n=>new MapPoint2D(n.X,n.Y)).Concat(map.Edges.SelectMany(e=>e.Points)).ToArray();var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
            _creationMapScale=Math.Min(840/Math.Max(1,maxX-minX),440/Math.Max(1,maxY-minY));_creationMapMidX=(minX+maxX)/2;_creationMapMidY=(minY+maxY)/2;
            CreationMapRoads=map.Edges.Select(e=>new TaskCreationMapRoad(CreationMapGeometry(e.Points))).ToArray();
            CreationMapDots=map.Nodes.Select(n=>{var p=CreationMapPoint(n.X,n.Y);return new TaskCreationMapDot(p.X-5,p.Y-5,$"业务 {n.Id} / 厂家 {n.VendorId}\n本地X={n.X:G8}m Y={n.Y:G8}m");}).ToArray();
            var strokes=new List<TaskCreationRouteMapStroke>();
            foreach(var segment in preview.Segments)
            {
                if(!TryCreationMapEdge(segment,out var edge,out var reverse))continue;
                var path=(reverse?edge.Points.Reverse():edge.Points).ToArray();var color=segment.Status=="不符"?"#EF4444":segment.Status!="本次通过"?"#D09B2E":segment.Sequence switch{1=>"#168BFF",3=>"#CB7800",5=>"#D34ADB",_=>"#159B72"};
                strokes.Add(new(CreationMapGeometry(path),CreationMapArrow(path),color,$"第{segment.Sequence}步 / 子段{segment.Segment} · {segment.EdgeId}\n{segment.LogicalPair}\n厂家 {segment.VendorPair}\n{segment.Status}：{segment.Message}",segment));
            }
            CreationMapRoutes=strokes.OrderBy(s=>s.Segment.Status=="不符"?1:0).ToArray();
            CreationRouteMapMessage=$"路线定位{strokes.Count}/{preview.Segments.Count}段；红=不符，金=未完整核验；通过段按运输阶段着色。缺有效几何不补线。{CreationMapContext}";
            RaiseCreationMapChanges();LocateFailedCreationRouteCommand.RaiseCanExecuteChanged();
        }
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or NullReferenceException){ClearCreationRouteMap("地图定位不可用："+ex.Message);}
    }
    public async Task RecheckCreationRouteMapContextAsync(CancellationToken ct)
    {
        if(_creationMap is not {} map)return;var generation=_routePreviewGeneration;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            // This endpoint reads backend map/task/PLC/AGV caches, never sends new Robokit requests.
            var options=await _client.GetTaskCreationOptionsAsync(budget.Token);budget.Token.ThrowIfCancellationRequested();
            if(generation!=_routePreviewGeneration||!ReferenceEquals(map,_creationMap))return;
            var vehicle=options.Vehicles.FirstOrDefault(v=>v.VehicleId==_creationMapVehicle);var station=options.Stations.FirstOrDefault(s=>s.StationId==_creationMapStation);
            if(options.MapId!=map.MapId||options.MapVersionId!=map.VersionId||options.MapRuntimeId!=map.RuntimeId||options.ParameterRevision!=_creationMapRevision||options.Available!=map.SourceOptionsAvailable||vehicle is null||vehicle.Available!=map.SourceVehicleAvailable||vehicle.CurrentSite!=_creationMapOrigin||station is null||station.Available!=map.SourceStationAvailable||station.ConfigurationValid!=map.SourceStationConfigurationValid)ClearCreationRoutePreview("后台地图实例、参数、车辆/工位条件已变化，旧路线地图已清除，请刷新选项重查。");
        }
        catch(Exception)
        {
            if(generation==_routePreviewGeneration&&ReferenceEquals(map,_creationMap))ClearCreationRoutePreview("无法复核当前后台地图上下文，旧地图已清除，请重查。");
        }
    }
    private bool TryCreationMapEdge(ManualTaskRouteSegment segment,out ManualTaskRouteMapEdge edge,out bool reverse)
    {
        reverse=false;edge=null!;
        if(!_creationMapEdges.TryGetValue(segment.EdgeId,out edge!)||!_creationMapNodes.TryGetValue(segment.LogicalFrom,out var from)||!_creationMapNodes.TryGetValue(segment.LogicalTo,out var to)||from.VendorId!=segment.VendorFrom||to.VendorId!=segment.VendorTo)return false;
        if(edge.From==segment.LogicalFrom&&edge.To==segment.LogicalTo)return true;
        reverse=true;return edge.Bidirectional&&edge.To==segment.LogicalFrom&&edge.From==segment.LogicalTo;
    }
    public void LocateCreationRouteSegment(ManualTaskRouteSegment? segment)
    {
        _selectedCreationRouteSegment=null;CreationMapSelectedGeometry=Geometry.Empty;CreationMapSelectedArrow=Geometry.Empty;
        if(segment is null){RaiseCreationMapChanges();return;}
        ExpireCreationRoutePreview();
        CreationRouteMapOpen=true;
        if(_creationMap is null||!CreationRouteSegments.Any(s=>ReferenceEquals(s,segment))){CreationRouteMapMessage="该行不属于当前有效地图观察，未定位。";RaiseCreationMapChanges();return;}
        if(!TryCreationMapEdge(segment,out var edge,out var reverse)){CreationRouteMapMessage=$"{segment.EdgeId} 缺少一致的几何、方向或业务/厂家端点，未补画路线。原因：{segment.Message}";RaiseCreationMapChanges();return;}
        _selectedCreationRouteSegment=segment;CreationMapSelectedGeometry=CreationMapGeometry(edge.Points);CreationMapSelectedArrow=CreationMapArrow((reverse?edge.Points.Reverse():edge.Points).ToArray());
        var points=edge.Points.Select(p=>CreationMapPoint(p.X,p.Y)).ToArray();var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
        CreationMapZoom=Math.Clamp(Math.Min(800/Math.Max(1,maxX-minX),420/Math.Max(1,maxY-minY)),.5,64);CreationMapPanX=(450-(minX+maxX)/2)*CreationMapZoom;CreationMapPanY=(250-(minY+maxY)/2)*CreationMapZoom;
        CreationRouteMapMessage=$"第{segment.Sequence}步 / 子段{segment.Segment} · {segment.EdgeId} · {segment.Status}\n业务 {segment.LogicalPair}；厂家 {segment.VendorPair}\n原因：{segment.Message}\n{CreationMapContext}";RaiseCreationMapChanges();
    }
    private Geometry CreationMapArrow(IReadOnlyList<MapPoint2D> path)
    {
        var points=path.Select(p=>CreationMapPoint(p.X,p.Y)).ToArray();var lengths=Enumerable.Range(1,points.Length-1).Select(i=>(points[i]-points[i-1]).Length).ToArray();var total=lengths.Sum();if(total<1)return Geometry.Empty;
        var target=total/2;double traversed=0;
        for(var i=0;i<lengths.Length;i++)
        {
            var length=lengths[i];if(length<1e-9)continue;if(traversed+length<target){traversed+=length;continue;}
            var tangent=(points[i+1]-points[i])/length;var center=points[i]+tangent*(target-traversed);var side=new Vector(-tangent.Y,tangent.X);var size=Math.Min(12,total*.2);
            var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(center+tangent*size*.6,true,true);context.LineTo(center-tangent*size*.5+side*size*.45,true,false);context.LineTo(center-tangent*size*.5-side*size*.45,true,false);}geometry.Freeze();return geometry;
        }
        return Geometry.Empty;
    }
}
