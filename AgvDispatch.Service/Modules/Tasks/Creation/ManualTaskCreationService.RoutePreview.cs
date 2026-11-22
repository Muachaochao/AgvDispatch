using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed partial class ManualTaskCreationService
{
    public async Task<ManualTaskRoutePreview> PreviewRoutesAsync(ManualTaskRoutePreviewRequest request,CancellationToken ct)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(20));var token=budget.Token;
        var legs=new List<ManualTaskRouteLeg>();var segments=new List<ManualTaskRouteSegment>();var current="";
        var observationId=Guid.NewGuid().ToString("N");ActiveMapRuntimeSnapshot? geometrySource=null;
        IReadOnlyList<ManualTaskStepPreview> stepSource=[];
        bool sourceAvailable=false,sourceVehicle=false,sourceStation=false,sourceConfiguration=false;
        ManualTaskRoutePreview Result(bool verified,string message)
        {
            ManualTaskRouteMap? geometry=null;var issue="本次未取得稳定的运行图上下文。";
            if(geometrySource is {} source&&ReferenceEquals(source,activeMap.Current)&&parameters.Revision==request.ParameterRevision)
            {
                try{geometry=BuildTaskRouteMap(source,observationId,segments,token) with{SourceOptionsAvailable=sourceAvailable,SourceVehicleAvailable=sourceVehicle,SourceStationAvailable=sourceStation,SourceStationConfigurationValid=sourceConfiguration};issue="";}
                catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or System.IO.InvalidDataException){issue="导航几何不可用："+ex.Message;}
                if(!ReferenceEquals(source,activeMap.Current)||parameters.Revision!=request.ParameterRevision)
                {geometry=null;verified=false;issue="构建地图期间运行图或参数变化，未提供定位图。";message=issue;segments=segments.Select(s=>s with{Status="已失效",Message=issue}).ToList();}
            }
            static string Text(string value,int max)=>value.Length<=max?value:value[..max]+"…（显示已限长）";
            var result=new ManualTaskRoutePreview(request.MapId,request.MapVersionId,request.ParameterRevision,request.StationId,request.VehicleId,current,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),verified,Text(message,2048),legs.Select(l=>l with{LogicalPath=Text(l.LogicalPath,8192),VendorPath=Text(l.VendorPath,8192),Message=Text(l.Message,1024)}).ToArray(),segments.Select(s=>s with{Message=Text(s.Message,1024)}).ToArray()){ObservationId=observationId,Map=geometry,MapIssue=issue,Steps=geometry is null?[]:stepSource};
            var jsonOptions=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            token.ThrowIfCancellationRequested();
            if(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result,jsonOptions).Length>8*1024*1024)
            {
                result=result with{Map=null,Steps=[],MapIssue="同批几何超过8MiB显示预算，未返回地图。"};
                if(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result,jsonOptions).Length>8*1024*1024)result=result with{Verified=false,Legs=[],Segments=[],Message="本次预览超过8MiB响应预算，未提供明细。任务提交仍需独立核验。"};
            }
            return result;
        }
        static bool Valid(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max;
        if(!Valid(request.StationId,32)||!Valid(request.VehicleId,32)||!Valid(request.MapId,64)||!Valid(request.MapVersionId,64)||!Valid(request.ParameterRevision,128))return Result(false,"工位、车辆、地图版本或参数修订无效。");
        ManualTaskCreationOptions options;ActiveMapRuntimeSnapshot? map;
        using(await gate.EnterControlAsync(token)){map=activeMap.Current;options=await GetOptionsCoreAsync(token);}
        if(map is null||map.MapId!=request.MapId||map.VersionId!=request.MapVersionId||parameters.Revision!=request.ParameterRevision)return Result(false,"运行地图或任务参数已变化，请刷新创建选项。");
        var station=options.Stations.FirstOrDefault(s=>s.StationId==request.StationId);var vehicle=options.Vehicles.FirstOrDefault(v=>v.VehicleId==request.VehicleId);
        if(station is null||vehicle is null)return Result(false,"所选工位或车辆不属于当前后台配置。");
        stepSource=station.PreviewSteps.ToArray();
        current=vehicle.CurrentSite;geometrySource=map;sourceAvailable=options.Available;sourceVehicle=vehicle.Available;sourceStation=station.Available;sourceConfiguration=station.ConfigurationValid;
        var nodes=map.Definition.Nodes.ToDictionary(n=>n.Id,StringComparer.Ordinal);
        MapNode? Resolve(string id){var found=map.Definition.Nodes.Where(n=>n.Enabled&&MapRouteSiteMapper.IsSameNode(map.Definition,n.Id,id)).ToArray();return found.Length==1?found[0]:null;}
        var pairs=new[]{(1,"车位 → 取货",current,station.PickupSite),(3,"取货 → 等待",station.PickupSite,options.WaitingSite),(5,"等待 → 倒料",options.WaitingSite,options.DumpingSite),(7,"倒料 → 取货",options.DumpingSite,station.PickupSite)};
        var localValid=true;
        foreach(var (sequence,name,source,target) in pairs)
        {
            token.ThrowIfCancellationRequested();var from=Resolve(source);var to=Resolve(target);var route=from is null||to is null?null:sequence==1?map.Graph.FindShortestTransportRoute(from.Id,to.Id,forbiddenIntermediateNodes:PickupExcludedNodes(map,request.VehicleId,from.Id)):map.Graph.FindShortestRoute(from.Id,to.Id);
            if(route is null||route.Nodes.Count is <2 or >129||route.EdgeIds.Count!=route.Nodes.Count-1){legs.Add(new(sequence,name,source+" → "+target,"",route is null&&sequence==1&&from is not null&&from.Id==to?.Id?"车辆已在取货点；缺少避开等待/倒料节点的真实已启用进出闭合路线，禁止同点空路径代替取箱动作。":"无可执行本地路线、端点不唯一或超过本页每段运输128子段的显示预算。"));localValid=false;continue;}
            var path=route.Nodes.Select(id=>nodes[id]).ToArray();
            var mapping=path.All(n=>!string.IsNullOrWhiteSpace(n.AgvSiteId)&&map.Definition.Nodes.Count(other=>other.Enabled&&other.AgvSiteId is not null&&MapRouteSiteMapper.IsSameNode(map.Definition,other.AgvSiteId,n.AgvSiteId))==1);
            legs.Add(new(sequence,name,string.Join(" → ",path.Select(n=>n.Id)),string.Join(" → ",path.Select(n=>n.AgvSiteId??"[未映射]")),mapping?(sequence==1&&from!.Id==to!.Id?"实际进出闭合路线；沿真实相邻边离开并返回，在末点执行取箱动作；厂家核验见逐段结果。":"本地静态路线；厂家核验见逐段结果。"):"存在缺失/重复厂家站点映射。"));
            localValid&=mapping;
            for(var i=0;i<path.Length-1;i++)
            {
                var invalid=path.Skip(i).Take(2).Where(n=>string.IsNullOrWhiteSpace(n.AgvSiteId)||map.Definition.Nodes.Count(other=>other.Enabled&&other.AgvSiteId is not null&&MapRouteSiteMapper.IsSameNode(map.Definition,other.AgvSiteId,n.AgvSiteId))!=1).Select(n=>n.Id).ToArray();
                segments.Add(new(sequence,i+1,route.EdgeIds[i],path[i].Id,path[i+1].Id,path[i].AgvSiteId??"",path[i+1].AgvSiteId??"",invalid.Length>0?"不符":"未核验",invalid.Length>0?"本地厂家映射缺失/重复："+string.Join("、",invalid)+"；尚未查询车端。":"尚未取得本次厂家直接线路观察。"));
            }
        }
        if(!localValid)return Result(false,"本地路径或厂家映射不完整，未查询车端；不创建任务、不申请路权。");
        if(!options.Available||station is not {Available:true,ConfigurationValid:true}||!vehicle.Available)return Result(false,(!options.Available?options.Explanation:!station.Available?station.Explanation:vehicle.Explanation)+" 仅展示本地路线，未查询车端。");
        if(robokitMap is null)return Result(false,"后台未配置Robokit车端核验，不能把本地可达视为厂家已确认。");
        var check=await robokitMap.CheckAsync(request.VehicleId,current,station.PickupSite,options.WaitingSite,options.DumpingSite,map.MapId,map.VersionId,token);
        token.ThrowIfCancellationRequested();
        ManualTaskCreationOptions latest;
        using(await gate.EnterControlAsync(token)){latest=await GetOptionsCoreAsync(token);}
        var latestVehicle=latest.Vehicles.FirstOrDefault(v=>v.VehicleId==request.VehicleId);var latestStation=latest.Stations.FirstOrDefault(s=>s.StationId==request.StationId);
        if(!ReferenceEquals(map,activeMap.Current)||parameters.Revision!=request.ParameterRevision||!gate.CanStartNewWork||!latest.Available||latestStation is not {Available:true,ConfigurationValid:true}||latestVehicle is not {Available:true}||!MapRouteSiteMapper.IsSameNode(map.Definition,latestVehicle.CurrentSite,current))
        {
            geometrySource=null;
            segments=segments.Select(s=>s with{Status="已失效",Message="查询期间地图、参数、工位或车辆状态变化，请重查。"}).ToList();
            return Result(false,"本次观察已失效；请刷新选项后重查。");
        }
        segments=segments.Select(segment=>
        {
            var observed=check.RouteChecks.Where(r=>r.Sequence==segment.Sequence&&r.Source==segment.VendorFrom&&r.Target==segment.VendorTo).ToArray();
            return observed.Length!=1?segment with{Message=check.Accepted?"整体核验未提供该段独立观察。":"未完成该段观察；"+check.Message}:segment with{Status=observed[0].DirectConfirmed?(check.Accepted?"本次通过":"仅已观察") :"不符",Message=observed[0].Message};
        }).ToList();
        var verified=check.Accepted&&segments.Count>0&&segments.All(s=>s.Status=="本次通过");
        return Result(verified,check.Message+" 这是只读观察，不预留路权、不创建任务；提交时独立重新核验。");
    }
}
