using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.Services;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record ReviewRouteSegment(string RoadId,string Role,int Step,string From,string To,string VendorFrom,string VendorTo,string Direction,string Kind,string Status,string Issues,string Details,string SnapshotSha256,MapReviewRoadDetail Road);

public sealed partial class ConnectionSettingsViewModel
{
    private ReviewRouteSegment? _selectedReviewSegment;
    private bool _reviewSegmentsOpen;
    public IReadOnlyList<ReviewRouteSegment> ReviewRouteSegments {get;private set;}=[];
    public bool ReviewSegmentsOpen {get=>_reviewSegmentsOpen;set=>SetProperty(ref _reviewSegmentsOpen,value);}
    public Geometry ReviewSelectedRoadGeometry {get;private set;}=Geometry.Empty;
    public string ReviewSegmentDetails {get;private set;}="选择子段查看端点、方向与曲线属性。";
    public ReviewRouteSegment? SelectedReviewSegment
    {
        get=>_selectedReviewSegment;
        set{if(ReferenceEquals(_selectedReviewSegment,value))return;SelectReviewSegment(value);}
    }
    private void ResetReviewSegments()
    {
        _selectedReviewSegment=null;ReviewRouteSegments=[];ReviewSegmentsOpen=false;ReviewSelectedRoadGeometry=Geometry.Empty;ReviewSegmentDetails="选择子段查看端点、方向与曲线属性。";
        RaisePropertyChanged(nameof(SelectedReviewSegment));RaisePropertyChanged(nameof(ReviewRouteSegments));RaisePropertyChanged(nameof(ReviewSelectedRoadGeometry));RaisePropertyChanged(nameof(ReviewSegmentDetails));
    }
    public void SelectReviewSegment(ReviewRouteSegment? segment)
    {
        _selectedReviewSegment=null;ReviewSelectedRoadGeometry=Geometry.Empty;
        if(segment is null)ReviewSegmentDetails="选择子段查看端点、方向与曲线属性。";
        else if(MapReview?.Snapshot is not {} snapshot||segment.SnapshotSha256!=snapshot.Sha256||!ReviewRouteSegments.Any(s=>ReferenceEquals(s,segment)))ReviewSegmentDetails="子段已过期或不属于当前高亮，已清除选择。";
        else
        {
            _selectedReviewSegment=segment;ReviewSegmentsOpen=true;ReviewSelectedRoadGeometry=ReviewRoadGeometry(segment.Road.Points);ReviewSegmentDetails=segment.Details;FocusReviewRoads([segment.Road]);
        }
        RaisePropertyChanged(nameof(SelectedReviewSegment));RaisePropertyChanged(nameof(ReviewSelectedRoadGeometry));RaisePropertyChanged(nameof(ReviewSegmentDetails));
    }
    private ReviewRouteOverlay CreateReviewOverlay(MapReviewRoadDetail road,string color,MapReviewRoute? route)
    {
        var snapshot=MapReview!.Snapshot!;
        var from=route?.LogicalFrom??road.From;var to=route?.LogicalTo??road.To;
        var a=snapshot.Nodes.First(n=>n.Id==from);var b=snapshot.Nodes.First(n=>n.Id==to);
        var reversed=route is not null&&road.From!=from;
        var ordered=(reversed?road.Points.Reverse():road.Points).ToArray();
        var screen=ordered.Select(p=>ReviewPoint(p.X,p.Y)).ToArray();
        var arrows=new GeometryGroup();
        if(route is null&&road.Bidirectional){arrows.Children.Add(ReviewArrow(screen,.35));arrows.Children.Add(ReviewArrow(screen.Reverse().ToArray(),.35));}
        else arrows.Children.Add(ReviewArrow(screen,.5));
        arrows.Freeze();
        static string Number(double v)=>v.ToString("G8",CultureInfo.InvariantCulture);
        var kind=road.GeometryKind switch{MapEdgeGeometryKind.Line=>"直线",MapEdgeGeometryKind.CircularArc=>"圆弧",MapEdgeGeometryKind.CubicBezier=>"三阶Bezier",_=>road.GeometryKind.ToString()};
        var direction=route is null?(road.Bidirectional?"本地双向允许（非任务行驶方向）":"本地单向允许（非任务行驶方向）"):(reversed?"运输反向通过显式双向边":"运输方向");
        var status=route?.Status??"未关联运输结果";var issues=route?.Issues??"";
        var tooltip=$"{route?.RoleText??"路段搜索"} · 第{route?.Step??0}段 · {from} → {to} · {road.Id} · 报告声明{status}：{issues}";
        var controls=road.ControlPoints.Count==0?"无":string.Join("；",road.ControlPoints.Select(p=>$"({Number(p.X)}, {Number(p.Y)})m"));
        var details=$"路段：{road.Id}\n角色：{route?.RoleText??"路段搜索"} / 子段：{(route?.Step.ToString()??"未关联")}\n业务：{from} → {to}\n厂家：{a.AgvSiteId??"未提供"} → {b.AgvSiteId??"未提供"}\n本地起点：({Number(a.X)}, {Number(a.Y)})m\n本地终点：({Number(b.X)}, {Number(b.Y)})m\n{direction}；原边 {road.From} {(road.Bidirectional?"↔":"→")} {road.To}\n几何：{kind}；控制/过弧点：{controls}\n报告声明：{status}\n差异：{(string.IsNullOrEmpty(issues)?"未列出":issues)}\n箭头按采样曲线方向显示，不表示车头朝向或车体安全包络。{ReviewSource}";
        var segment=new ReviewRouteSegment(road.Id,route?.RoleText??"路段搜索",route?.Step??0,from,to,a.AgvSiteId??"",b.AgvSiteId??"",direction,kind,status,issues,details,snapshot.Sha256,road);
        return new(ReviewRoadGeometry(road.Points),color,tooltip){Arrows=arrows,Segment=segment};
    }
    private void PublishReviewOverlays(IReadOnlyList<ReviewRouteOverlay> overlays)
    {
        ReviewRouteHighlights=overlays;ReviewRouteSegments=overlays.Select(o=>o.Segment!).ToArray();ReviewSegmentsOpen=true;
        RaisePropertyChanged(nameof(ReviewRouteHighlights));RaisePropertyChanged(nameof(ReviewRouteSegments));
    }
    /// <summary>Screen-space tangent at a length fraction; zero-length repeated samples cannot produce a direction.</summary>
    private static Geometry ReviewArrow(IReadOnlyList<Point> points,double fraction)
    {
        if(points.Count<2)return Geometry.Empty;
        var lengths=Enumerable.Range(1,points.Count-1).Select(i=>(points[i]-points[i-1]).Length).ToArray();var total=lengths.Sum();
        if(!double.IsFinite(total)||total<1)return Geometry.Empty;
        var target=total*fraction;double traversed=0;
        for(int i=0;i<lengths.Length;i++)
        {
            var length=lengths[i];if(length<1e-9)continue;
            if(traversed+length<target){traversed+=length;continue;}
            var tangent=(points[i+1]-points[i])/length;var center=points[i]+tangent*(target-traversed);var size=Math.Min(12,total*.2);var side=new Vector(-tangent.Y,tangent.X);
            var tip=center+tangent*size*.6;var left=center-tangent*size*.5+side*size*.45;var right=center-tangent*size*.5-side*size*.45;
            var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(tip,true,true);context.LineTo(left,true,false);context.LineTo(right,true,false);}geometry.Freeze();return geometry;
        }
        return Geometry.Empty;
    }
}
