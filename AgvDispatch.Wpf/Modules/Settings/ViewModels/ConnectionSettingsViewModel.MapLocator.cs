using System.Windows;
using System.Windows.Media;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.Services;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record ReviewMapDot(string Id,string VendorId,double Left,double Top,string Tooltip);
public sealed record ReviewMapRoad(Geometry Geometry);
public sealed record ReviewMapHit(string Id,string VendorId,double X,double Y,string SnapshotSha256){public bool IsRoad {get;init;} public int RoadIndex {get;init;}=-1;}

public sealed partial class ConnectionSettingsViewModel
{
    private string _reviewQuery="",_reviewSearchMode="自动",_reviewLocationMessage="尚未导入报告。";
    private bool _reviewLocatorOpen,_reviewHasMarker;
    private double _reviewZoom=1,_reviewPanX,_reviewPanY,_reviewMarkerX,_reviewMarkerY,_reviewFit=1,_reviewMidX,_reviewMidY;
    private MapReviewStation? _selectedReviewStation;
    private ReviewMapHit? _selectedReviewHit;
    private MapNode? _locatedReviewNode;
    public string ReviewSearchQuery {get=>_reviewQuery;set=>SetProperty(ref _reviewQuery,value??"");}
    public IReadOnlyList<string> ReviewSearchModes {get;}=["自动","业务编号","厂家编号","路段编号"];
    public string ReviewSearchMode {get=>_reviewSearchMode;set=>SetProperty(ref _reviewSearchMode,value);}
    public string ReviewLocationMessage {get=>_reviewLocationMessage;private set=>SetProperty(ref _reviewLocationMessage,value);}
    public bool ReviewLocatorOpen {get=>_reviewLocatorOpen;set=>SetProperty(ref _reviewLocatorOpen,value);}
    public bool ReviewHasMarker {get=>_reviewHasMarker;private set=>SetProperty(ref _reviewHasMarker,value);}
    public double ReviewZoom {get=>_reviewZoom;private set=>SetProperty(ref _reviewZoom,value);}
    public double ReviewPanX {get=>_reviewPanX;private set=>SetProperty(ref _reviewPanX,value);}
    public double ReviewPanY {get=>_reviewPanY;private set=>SetProperty(ref _reviewPanY,value);}
    public double ReviewMarkerX {get=>_reviewMarkerX;private set=>SetProperty(ref _reviewMarkerX,value);}
    public double ReviewMarkerY {get=>_reviewMarkerY;private set=>SetProperty(ref _reviewMarkerY,value);}
    public IReadOnlyList<ReviewMapDot> ReviewMapDots {get;private set;}=[];
    public IReadOnlyList<ReviewMapRoad> ReviewMapRoads {get;private set;}=[];
    public IReadOnlyList<ReviewMapHit> ReviewSearchResults {get;private set;}=[];
    public MapReviewStation? SelectedReviewStation {get=>_selectedReviewStation;set{if(ReferenceEquals(_selectedReviewStation,value))return;_selectedReviewStation=value;RaisePropertyChanged();if(value is not null)LocateReviewStation(value);else ClearReviewMarker();}}
    public ReviewMapHit? SelectedReviewHit {get=>_selectedReviewHit;set{if(ReferenceEquals(_selectedReviewHit,value))return;_selectedReviewHit=value;RaisePropertyChanged();if(value is not null)LocateReviewHit(value);}}
    public DelegateCommand SearchReviewMapCommand {get;private set;}=null!;
    public DelegateCommand FitReviewMapCommand {get;private set;}=null!;
    public DelegateCommand ZoomInReviewMapCommand {get;private set;}=null!;
    public DelegateCommand ZoomOutReviewMapCommand {get;private set;}=null!;
    private void InitializeReviewLocator()
    {
        SearchReviewMapCommand=new DelegateCommand(SearchReviewMap);
        FitReviewMapCommand=new DelegateCommand(()=>{ReviewZoom=1;ReviewPanX=0;ReviewPanY=0;});
        ZoomInReviewMapCommand=new DelegateCommand(()=>AdjustReviewZoom(1.5));
        ZoomOutReviewMapCommand=new DelegateCommand(()=>AdjustReviewZoom(1/1.5));
    }
    private string ReviewSource=>MapReview is not { } r?"无报告":$"只读{(r.Mode=="synthetic"?"合成模拟":"声明采集")}快照；地图/版本：{r.LocalMapId}/{r.LocalMapVersion}；不联动当前运行地图。";
    private void ResetReviewLocator()
    {
        ClearReviewHighlights();ReviewRouteRole="全部四段";_selectedReviewRoute=null;RaisePropertyChanged(nameof(SelectedReviewRoute));
        _selectedReviewStation=null;_selectedReviewHit=null;_locatedReviewNode=null;
        RaisePropertyChanged(nameof(SelectedReviewStation));RaisePropertyChanged(nameof(SelectedReviewHit));
        ReviewSearchResults=[];RaisePropertyChanged(nameof(ReviewSearchResults));ReviewSearchQuery="";ReviewHasMarker=false;ReviewLocatorOpen=false;ReviewZoom=1;ReviewPanX=0;ReviewPanY=0;
        ReviewMapDots=[];ReviewMapRoads=[];
        var snapshot=MapReview?.Snapshot;
        if(snapshot is not null&&snapshot.Nodes.Count>0)
        {
            var points=snapshot.Nodes.Select(n=>new MapPoint2D(n.X,n.Y)).Concat(snapshot.Roads.SelectMany(r=>r)).ToArray();
            var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
            _reviewFit=Math.Min(852/Math.Max(1,maxX-minX),472/Math.Max(1,maxY-minY));_reviewMidX=(minX+maxX)/2;_reviewMidY=(minY+maxY)/2;
            var roads=new List<ReviewMapRoad>();foreach(var road in snapshot.Roads)
            {
                var pointsOnCanvas=road.Select(p=>ReviewPoint(p.X,p.Y)).ToArray();if(pointsOnCanvas.Length<2)continue;
                var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(pointsOnCanvas[0],false,false);context.PolyLineTo(pointsOnCanvas.Skip(1).ToArray(),true,false);}geometry.Freeze();roads.Add(new(geometry));
            }
            ReviewMapRoads=roads;BuildReviewDots();
            ReviewLocationMessage=$"{snapshot.Name} · 本地米制坐标；节点显示{ReviewMapDots.Count}/{snapshot.Nodes.Count}，道路显示{snapshot.Roads.Count}/{snapshot.TotalRoads}（无效/超预算省略{snapshot.SkippedRoads}）。{ReviewSource}";
        }
        else ReviewLocationMessage=MapReview is null?"尚未导入报告。":(string.IsNullOrEmpty(MapReview.SnapshotIssue)?"快照没有可定位节点。":MapReview.SnapshotIssue);
        RaisePropertyChanged(nameof(ReviewMapRoads));RaisePropertyChanged(nameof(ReviewMapDots));
    }
    private Point ReviewPoint(double x,double y)=>new(450+(x-_reviewMidX)*_reviewFit,260-(y-_reviewMidY)*_reviewFit);
    private void BuildReviewDots()
    {
        var source=MapReview?.Snapshot?.Nodes??[];var visible=source.Take(2000).ToList();
        if(_locatedReviewNode is { } selected&&!visible.Contains(selected))visible.Add(selected);
        ReviewMapDots=visible.Select(n=>{var p=ReviewPoint(n.X,n.Y);return new ReviewMapDot(n.Id,n.AgvSiteId??"",p.X-5,p.Y-5,$"业务：{n.Id}\n厂家：{n.AgvSiteId}\n本地X={n.X:G8}m Y={n.Y:G8}m");}).ToArray();RaisePropertyChanged(nameof(ReviewMapDots));
    }
    private void ClearReviewMarker(){_locatedReviewNode=null;ReviewHasMarker=false;BuildReviewDots();}
    public void LocateReviewStation(MapReviewStation row)
    {
        ClearReviewHighlights();ClearReviewMarker();ReviewLocatorOpen=true;
        if(MapReview?.Snapshot is not { } snapshot){ReviewLocationMessage=MapReview?.SnapshotIssue is {Length:>0} issue?issue:"报告缺少可定位本地快照。";return;}
        if(!MapReview.Stations.Any(s=>ReferenceEquals(s,row))){ReviewLocationMessage="该行不属于当前报告，已取消定位。";return;}
        var candidates=snapshot.Nodes.Where(n=>(string.IsNullOrEmpty(row.LogicalId)||n.Id==row.LogicalId)&&n.AgvSiteId==row.SiteId).ToArray();
        if(candidates.Length!=1||snapshot.Nodes.Count(n=>n.AgvSiteId==row.SiteId)!=1){ReviewLocationMessage="业务/厂家编号在快照中不唯一或不对应，已取消定位。";return;}
        FocusReviewNode(candidates[0]);
    }
    private void FocusReviewNode(MapNode node)
    {
        _locatedReviewNode=node;var point=ReviewPoint(node.X,node.Y);ReviewMarkerX=point.X-12;ReviewMarkerY=point.Y-12;ReviewHasMarker=true;ReviewZoom=2;ReviewPanX=(450-point.X)*ReviewZoom;ReviewPanY=(260-point.Y)*ReviewZoom;BuildReviewDots();
        ReviewLocationMessage=$"已定位：业务 {node.Id} / 厂家 {node.AgvSiteId}；本地X={node.X:G8}m，Y={node.Y:G8}m。{ReviewSource} 节点显示{ReviewMapDots.Count}/{MapReview!.Snapshot!.Nodes.Count}；不代表坐标对齐核定。";
    }
    public void SearchReviewMap()
    {
        ClearReviewHighlights();ClearReviewMarker();_selectedReviewHit=null;RaisePropertyChanged(nameof(SelectedReviewHit));ReviewSearchResults=[];RaisePropertyChanged(nameof(ReviewSearchResults));ReviewLocatorOpen=true;
        if(MapReview?.Snapshot is not { } snapshot){ReviewLocationMessage="请先导入含可用业务地图快照的报告。";return;}
        var query=ReviewSearchQuery.Trim();if(query.Length is 0 or >256||!ReviewSearchModes.Contains(ReviewSearchMode)){ReviewLocationMessage="请输入1～256字符编号并选择搜索类型。";return;}
        if(ReviewSearchMode=="路段编号"){SearchReviewRoads(snapshot,query);return;}
        bool Match(MapNode n,bool exact)=>
            (ReviewSearchMode!="厂家编号"&&(exact?n.Id.Equals(query,StringComparison.OrdinalIgnoreCase):n.Id.Contains(query,StringComparison.OrdinalIgnoreCase)))||
            (ReviewSearchMode!="业务编号"&&(exact?string.Equals(n.AgvSiteId,query,StringComparison.OrdinalIgnoreCase):n.AgvSiteId?.Contains(query,StringComparison.OrdinalIgnoreCase)==true));
        var exact=snapshot.Nodes.Where(n=>Match(n,true)).ToArray();var found=exact.Length>0?exact:snapshot.Nodes.Where(n=>Match(n,false)).ToArray();
        ReviewSearchResults=found.Take(200).Select(n=>new ReviewMapHit(n.Id,n.AgvSiteId??"",n.X,n.Y,snapshot.Sha256)).ToArray();RaisePropertyChanged(nameof(ReviewSearchResults));
        if(exact.Length==1){SelectedReviewHit=ReviewSearchResults[0];return;}
        ReviewLocationMessage=$"命中{found.Length}项，展示前{ReviewSearchResults.Count}项；{(exact.Length>1?"业务/厂家编号存在多个精确候选":"请选择候选行定位")}。{ReviewSource}";
    }
    private void LocateReviewHit(ReviewMapHit hit)
    {
        ClearReviewHighlights();ClearReviewMarker();if(MapReview?.Snapshot is not { } snapshot||hit.SnapshotSha256!=snapshot.Sha256||!ReviewSearchResults.Any(h=>ReferenceEquals(h,hit))){ReviewLocationMessage="候选已过期或不属于当前报告。";return;}
        ClearReviewHighlights();if(hit.IsRoad){LocateReviewRoad(snapshot,hit);return;}
        var node=snapshot.Nodes.SingleOrDefault(n=>n.Id==hit.Id&&(n.AgvSiteId??"")==hit.VendorId);
        if(node is null){ReviewLocationMessage="候选与当前快照不一致。";return;}FocusReviewNode(node);
    }
    public void AdjustReviewZoom(double factor)
    {
        if(!double.IsFinite(factor)||factor<=0)return;var next=Math.Clamp(ReviewZoom*factor,.5,8);var ratio=next/ReviewZoom;ReviewPanX*=ratio;ReviewPanY*=ratio;ReviewZoom=next;
    }
}
