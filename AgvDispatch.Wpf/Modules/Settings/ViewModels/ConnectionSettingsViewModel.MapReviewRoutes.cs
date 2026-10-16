using System.Windows.Media;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.Services;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record ReviewRouteOverlay(Geometry Geometry, string Color, string Tooltip)
{
    public Geometry Arrows {get;init;}=Geometry.Empty;
    public ReviewRouteSegment? Segment {get;init;}
}
public sealed partial class ConnectionSettingsViewModel
{
    public IReadOnlyList<ReviewRouteOverlay> ReviewRouteHighlights {get;private set;}=[];
    public IReadOnlyList<string> ReviewRouteRoles {get;}=["全部四段","车位 → 取货","取货 → 等待","等待 → 倒料","倒料 → 取货"];
    private string _reviewRouteRole="全部四段";
    public string ReviewRouteRole {get=>_reviewRouteRole;set{if(SetProperty(ref _reviewRouteRole,value)){ClearReviewHighlights();ReviewLocationMessage="运输角色已变化，请点击高亮运输路线。";}}}
    private MapReviewRoute? _selectedReviewRoute;
    public MapReviewRoute? SelectedReviewRoute {get=>_selectedReviewRoute;set{if(ReferenceEquals(value,_selectedReviewRoute))return;_selectedReviewRoute=value;RaisePropertyChanged();if(value is not null)LocateReviewRoute(value);}}
    public DelegateCommand HighlightReviewRoutesCommand=>new(()=>HighlightReviewRoutes());
    private void ClearReviewHighlights(){ReviewRouteHighlights=[];RaisePropertyChanged(nameof(ReviewRouteHighlights));ResetReviewSegments();}
    private Geometry ReviewRoadGeometry(IReadOnlyList<MapPoint2D> points)
    {
        var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(ReviewPoint(points[0].X,points[0].Y),false,false);context.PolyLineTo(points.Skip(1).Select(p=>ReviewPoint(p.X,p.Y)).ToArray(),true,false);}geometry.Freeze();return geometry;
    }
    private void FocusReviewRoads(IEnumerable<MapReviewRoadDetail> roads)
    {
        var points=roads.SelectMany(r=>r.Points).Select(p=>ReviewPoint(p.X,p.Y)).ToArray();
        var minX=points.Min(p=>p.X);var maxX=points.Max(p=>p.X);var minY=points.Min(p=>p.Y);var maxY=points.Max(p=>p.Y);
        ReviewZoom=Math.Clamp(Math.Min(800/Math.Max(1,maxX-minX),440/Math.Max(1,maxY-minY)),.5,8);
        ReviewPanX=(450-(minX+maxX)/2)*ReviewZoom;ReviewPanY=(260-(minY+maxY)/2)*ReviewZoom;
    }
    private void SearchReviewRoads(MapReviewSnapshot snapshot,string query)
    {
        var indexed=snapshot.RoadDetails.Select((road,index)=>(road,index)).ToArray();
        var exact=indexed.Where(r=>r.road.Id.Equals(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        var found=exact.Length>0?exact:indexed.Where(r=>r.road.Id.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        ReviewSearchResults=found.Take(200).Select(r=>new ReviewMapHit(r.road.Id,$"{r.road.From} → {r.road.To}",0,0,snapshot.Sha256){IsRoad=true,RoadIndex=r.index}).ToArray();RaisePropertyChanged(nameof(ReviewSearchResults));
        if(exact.Length==1){SelectedReviewHit=ReviewSearchResults[0];return;}
        ReviewLocationMessage=$"路段命中{found.Length}项，展示前{ReviewSearchResults.Count}项；重名或部分匹配请选行。无效几何不列入可定位道路。{ReviewSource}";
    }
    private void LocateReviewRoad(MapReviewSnapshot snapshot,ReviewMapHit hit)
    {
        if(hit.RoadIndex<0||hit.RoadIndex>=snapshot.RoadDetails.Count){ReviewLocationMessage="路段候选无效。";return;}
        var road=snapshot.RoadDetails[hit.RoadIndex];if(road.Id!=hit.Id){ReviewLocationMessage="路段候选不匹配。";return;}
        PublishReviewOverlays([CreateReviewOverlay(road,"#F28C28",null)]);FocusReviewRoads([road]);
        ReviewLocationMessage=$"已定位路段 {road.Id}：{road.From} {(road.Bidirectional?"↔":"→")} {road.To}。仅展示本地快照几何，不表示可执行。{ReviewSource}";
    }
    public void LocateReviewRoute(MapReviewRoute row)
    {
        ClearReviewHighlights();ClearReviewMarker();ReviewLocatorOpen=true;
        if(MapReview is null||!MapReview.Routes.Any(r=>ReferenceEquals(r,row))){ReviewLocationMessage="运输行已过期或不属于当前报告。";return;}
        var roles=new[]{"vehicleToPick","pickToWait","waitToDump","dumpToPick"};var index=Array.IndexOf(roles,row.Role);
        if(index<0){ReviewLocationMessage="运输角色无效。";return;}
        ReviewRouteRole=ReviewRouteRoles[index+1];HighlightReviewRoutes();
    }
    public void HighlightReviewRoutes()
    {
        ClearReviewHighlights();ClearReviewMarker();ReviewLocatorOpen=true;
        if(MapReview?.Snapshot is not {} snapshot){ReviewLocationMessage="请先导入可用地图快照。";return;}
        if(snapshot.TotalRoads!=snapshot.RoadDetails.Count){ReviewLocationMessage="快照存在无效或缺失道路元数据，无法排除重复/冲突边，未高亮运输路线。";return;}
        var roleIndex=ReviewRouteRoles.ToList().IndexOf(ReviewRouteRole);
        if(roleIndex<0){ReviewLocationMessage="请选择四段运输角色。";return;}
        var roles=new[]{"vehicleToPick","pickToWait","waitToDump","dumpToPick"};var colors=new[]{"#168BFF","#CB7800","#D34ADB","#159B72"};
        if(MapReview.Routes.Count(r=>roleIndex==0||r.Role==roles[roleIndex-1])>2048){ReviewLocationMessage="所选运输超过2048段显示预算，未高亮。";return;}
        var nodeIndex=snapshot.Nodes.ToDictionary(n=>n.Id,StringComparer.Ordinal);
        var vendorCounts=snapshot.Nodes.GroupBy(n=>n.AgvSiteId??"",StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Count());
        var roadIds=snapshot.RoadDetails.GroupBy(r=>r.Id,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.OrdinalIgnoreCase);
        var direct=new Dictionary<(string,string),List<MapReviewRoadDetail>>();
        void Add(string a,string b,MapReviewRoadDetail road){if(!direct.TryGetValue((a,b),out var list))direct[(a,b)]=list=[];list.Add(road);}
        foreach(var road in snapshot.RoadDetails){Add(road.From,road.To,road);if(road.Bidirectional&&road.From!=road.To)Add(road.To,road.From,road);}
        var overlays=new List<ReviewRouteOverlay>();var matched=new List<MapReviewRoadDetail>();MapReviewRoute? previous=null;var failures=new List<string>();
        foreach(var role in roles.Where((_,i)=>roleIndex==0||i==roleIndex-1))
        {
            var rows=MapReview.Routes.Where(r=>r.Role==role).OrderBy(r=>r.Step).ToArray();
            if(rows.Length==0){failures.Add(role+"缺少运输段");continue;}
            for(var i=0;i<rows.Length;i++)
            {
                var row=rows[i];
                if(row.Step!=i+1||(previous is not null&&(previous.LogicalTo!=row.LogicalFrom||previous.VendorTo!=row.VendorFrom)))failures.Add(row.RoleText+"序号重复/不连续或端点不衔接");
                previous=row;
                if(string.IsNullOrWhiteSpace(row.VendorFrom)||string.IsNullOrWhiteSpace(row.VendorTo)||!nodeIndex.TryGetValue(row.LogicalFrom,out var from)||!nodeIndex.TryGetValue(row.LogicalTo,out var to)||from.AgvSiteId!=row.VendorFrom||to.AgvSiteId!=row.VendorTo||vendorCounts[row.VendorFrom]!=1||vendorCounts[row.VendorTo]!=1){failures.Add(row.RoleText+"业务/厂家端点缺失或不唯一");continue;}
                if(!direct.TryGetValue((row.LogicalFrom,row.LogicalTo),out var roads)||roads.Count!=1||roadIds[roads[0].Id]!=1){failures.Add(row.RoleText+"缺少唯一、方向相符的直接路段");continue;}
                var road=roads[0];matched.Add(road);overlays.Add(CreateReviewOverlay(road,colors[Array.IndexOf(roles,role)],row));
            }
        }
        if(failures.Count>0){ReviewLocationMessage=$"未高亮：{string.Join("；",failures.Take(3))}（共{failures.Count}项）。不猜路线、不显示部分成功。{ReviewSource}";return;}
        PublishReviewOverlays(overlays);FocusReviewRoads(matched);
        ReviewLocationMessage=$"{ReviewRouteRole}：已关联{overlays.Count}段；蓝=车位到取货，橙=取货到等待，紫=等待到倒料，绿=倒料返回。仅报告快照几何，报告差异仍保留，不代表当前路线或放行。{ReviewSource}";
    }
}
