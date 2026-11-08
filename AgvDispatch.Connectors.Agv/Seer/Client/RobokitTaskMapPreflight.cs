using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Connectors.Agv;

public sealed class RobokitTaskMapPreflight(AgvOptions options,AgvConnectionManager connections,
    IActiveMapRuntime runtime,IRobokitInspectionService inspection) : IRobokitTaskMapPreflight
{
    public async Task<RobokitTaskMapCheck> CheckCurrentMapAsync(string vehicleId,string mapId,string versionId,CancellationToken ct)
    {
        RobokitTaskMapCheck Refuse(string reason)=>new(false,"实际出区当前地图核验："+reason);
        var map=runtime.Current;
        if(map is null||map.MapId!=mapId||map.VersionId!=versionId)return Refuse("运行地图与原业务绑定不一致。");
        var matches=(options.MapBindings??[]).Where(b=>b.VehicleId==vehicleId&&b.LocalMapId==mapId&&b.LocalMapVersion==versionId).ToArray();
        if(matches.Length!=1)return Refuse("缺少唯一车辆/本地地图版本绑定。");
        var binding=matches[0];
        if(!binding.CoordinatesVerified||string.IsNullOrWhiteSpace(binding.RobotMap)||string.IsNullOrWhiteSpace(binding.RobotMapMd5)||
            binding.RobotMapMd5.Length!=32||binding.RobotMapMd5.Any(c=>!Uri.IsHexDigit(c)))return Refuse("车端地图名、32位MD5或坐标核定缺失。");
        var expectedMap=binding.RobotMap;var expectedMd5=binding.RobotMapMd5;
        if(!connections.TryGet(vehicleId,out _))return Refuse("车辆未配置。");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // The inspection obtains fresh 1300/1022/1302 observations and checks
            // identity again at the end. It never sends navigation or reserves resources.
            var observed=await inspection.ReadAsync(new(vehicleId),budget.Token);
            var afterBindings=(options.MapBindings??[]).Where(b=>b.VehicleId==vehicleId&&b.LocalMapId==mapId&&b.LocalMapVersion==versionId).ToArray();
            if(!observed.Available||!observed.Stable||observed.VehicleId!=vehicleId||observed.LocalMapId!=mapId||observed.LocalMapVersion!=versionId||
                observed.RobotMap!=expectedMap||!string.Equals(observed.RobotMapMd5,expectedMd5,StringComparison.OrdinalIgnoreCase)||
                observed.LoadMapStatus!=1||observed.Issues.Any(i=>i.Contains("1302"))||!ReferenceEquals(map,runtime.Current))
                return Refuse("本次车端地图、MD5、载入状态或稳定性未通过；"+observed.Reason);
            if(afterBindings.Length!=1||!afterBindings[0].CoordinatesVerified||afterBindings[0].RobotMap!=expectedMap||
                !string.Equals(afterBindings[0].RobotMapMd5,expectedMd5,StringComparison.OrdinalIgnoreCase))return Refuse("采集期间车辆坐标绑定变化。");
            return new(true,"实际出区当前车端地图、MD5与已核定坐标绑定一致。");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){return Refuse(ex is OperationCanceledException?"本次采集超时。":ex.Message);}
    }

    public async Task<RobokitTaskMapCheck> CheckRouteAsync(string vehicleId,IReadOnlyList<string> path,CancellationToken ct)
    {
        var map=runtime.Current;
        RobokitTaskMapCheck Refuse(string reason)=>new(false,"实际发送路线车端核验："+reason);
        var simulationLocal=string.Equals(options.TaskMapPreflightMode,"SimulationLocal",StringComparison.OrdinalIgnoreCase);
        if(map is null)return Refuse("没有运行地图。");
        var binding=options.MapBindings?.Where(b=>b.VehicleId==vehicleId && b.LocalMapId==map.MapId && b.LocalMapVersion==map.VersionId).ToArray();
        if(binding is not {Length:1} || !binding[0].CoordinatesVerified)return Refuse("缺少唯一已核定地图/坐标绑定。");
        if(!connections.TryGet(vehicleId,out var client))return Refuse("车辆未配置。");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var evidence=await inspection.ReadAsync(new(vehicleId),budget.Token);
            if(!evidence.Available || !evidence.Stable || evidence.RobotMap!=binding[0].RobotMap ||
                !string.Equals(evidence.RobotMapMd5,binding[0].RobotMapMd5,StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(binding[0].RobotMapMd5) || binding[0].RobotMapMd5.Length!=32 || binding[0].RobotMapMd5.Any(c=>!Uri.IsHexDigit(c)) || evidence.Issues.Any(i=>i.Contains("1302")))
                return Refuse("车端地图身份或载入状态未通过核验。");
            if(path.Count is < 2 or > 2048)return Refuse("路径节点数量无效。");
            var pathNodes=new List<MapNode>(path.Count);
            foreach(var id in path)
            {
                var nodes=string.IsNullOrWhiteSpace(id) ? [] :
                    map.Definition.Nodes.Where(n=>n.Enabled && n.AgvSiteId==id).ToArray();
                if(string.IsNullOrWhiteSpace(id) || evidence.Stations.Count(s=>s.Id==id)!=1 || nodes.Length!=1)
                    return Refuse("发送路径含缺失或不唯一厂家映射："+id);
                pathNodes.Add(nodes[0]);
            }
            if(simulationLocal)
            {
                for(var index=0;index<pathNodes.Count-1;index++)
                    if(!map.Definition.Edges.Any(e=>e.Enabled && e.From==pathNodes[index].Id && e.To==pathNodes[index+1].Id))
                        return Refuse($"隔离仿真同源地图缺少有向直接边：{path[index]}→{path[index+1]}。");
            }
            else
            {
                foreach(var pair in path.Zip(path.Skip(1)).Distinct())
                {
                    using var response=await client.RequestAsync(1303,new{source_id=pair.First,target_id=pair.Second},budget.Token);
                    if(!response.RootElement.TryGetProperty("list",out var list) || list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()!=2 ||
                        list[0].GetString()!=pair.First || list[1].GetString()!=pair.Second)return Refuse("实际线路未确认直接连接："+pair.First+"→"+pair.Second);
                }
            }
            using var after=await client.RequestAsync(1300,null,budget.Token);
            using var load=await client.RequestAsync(1022,null,budget.Token);
            if(!ReferenceEquals(map,runtime.Current) || after.RootElement.GetProperty("current_map").GetString()!=binding[0].RobotMap ||
                !string.Equals(after.RootElement.GetProperty("current_map_md5").GetString(),binding[0].RobotMapMd5,StringComparison.OrdinalIgnoreCase) ||
                load.RootElement.GetProperty("loadmap_status").GetInt32()!=1)return Refuse("采集期间地图变化或未完成载入。");
            return new(true,simulationLocal
                ? "隔离仿真车端地图身份通过；路线由同一已载入地图版本的有向边逐段核验。"
                : "实际发送路径核验通过。");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){return Refuse(ex.Message);}
    }
    public async Task<RobokitTaskMapCheck> CheckAsync(string vehicleId,string currentSite,string pickup,string waiting,string dumping,
        string mapId,string versionId,CancellationToken ct)
    {
        var checks=new List<RobokitTaskRouteCheck>();
        RobokitTaskMapCheck Refuse(string reason)=>new(false,"车端地图提交前核验："+reason){RouteChecks=checks.ToArray()};
        var map=runtime.Current;
        var simulationLocal=string.Equals(options.TaskMapPreflightMode,"SimulationLocal",StringComparison.OrdinalIgnoreCase);
        if(map is null || map.MapId!=mapId || map.VersionId!=versionId)return Refuse("后台运行地图已变化。");
        var matches=(options.MapBindings ?? []).Where(b=>b.VehicleId==vehicleId && b.LocalMapId==mapId && b.LocalMapVersion==versionId).ToArray();
        if(matches.Length!=1)return Refuse("缺少唯一的车辆/本地地图版本与车端地图绑定，请配置Agv:MapBindings。");
        var binding=matches[0];
        if(!binding.CoordinatesVerified || string.IsNullOrWhiteSpace(binding.RobotMap) || string.IsNullOrWhiteSpace(binding.RobotMapMd5) || binding.RobotMapMd5.Length!=32 ||
            binding.RobotMapMd5.Any(c=>!Uri.IsHexDigit(c)))return Refuse("尚未核定车端地图名称、32位MD5和坐标对应。");
        if(!connections.TryGet(vehicleId,out var client))return Refuse("车辆未配置。");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
        var token=budget.Token;
        try
        {
            var observation=await inspection.ReadAsync(new(vehicleId),token);
            if(!observation.Available || !observation.Stable || observation.LocalMapId!=mapId || observation.LocalMapVersion!=versionId)
                return Refuse(observation.Reason);
            if(observation.RobotMap!=binding.RobotMap || !string.Equals(observation.RobotMapMd5,binding.RobotMapMd5,StringComparison.OrdinalIgnoreCase) ||
                observation.Issues.Any(i=>i.Contains("1302")))return Refuse("车端地图/MD5与已核定绑定不符。");
            var sites=observation.Stations.GroupBy(s=>s.Id,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
            var routePairs=new[]{(currentSite,pickup),(pickup,waiting),(waiting,dumping),(dumping,pickup)};
            var checkedPairs=new HashSet<(string,string)>();
            for(var step=0;step<routePairs.Length;step++)
            {
                MapNode? Resolve(string id)
                {
                    var nodes=map.Definition.Nodes.Where(n=>n.Enabled && (n.Id.Equals(id,StringComparison.OrdinalIgnoreCase) || n.AgvSiteId==id)).ToArray();
                    return nodes.Length==1?nodes[0]:null;
                }
                var source=Resolve(routePairs[step].Item1);var target=Resolve(routePairs[step].Item2);
                var protectedNodes=new[]{Resolve(waiting)?.Id,Resolve(dumping)?.Id}.Where(id=>id is not null).Select(id=>id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var route=source is null || target is null ? null : step==0 ? map.Graph.FindShortestTransportRoute(source.Id,target.Id,forbiddenIntermediateNodes:protectedNodes) : map.Graph.FindShortestRoute(source.Id,target.Id);
                if(route is null || route.Nodes.Count is <2 or >2048)return Refuse(step==0&&source?.Id==target?.Id ? "第1步车辆已在取货点，但无避开等待/倒料节点的真实已启用进出闭合路线，禁止虚构同点运输。" : $"第{new[]{1,3,5,7}[step]}步没有可执行路线。");
                var path=new List<string>();
                foreach(var id in route.Nodes)
                {
                    var node=map.Definition.Nodes.Single(n=>n.Id==id);
                    if(string.IsNullOrWhiteSpace(node.AgvSiteId) || !sites.TryGetValue(node.AgvSiteId,out var count) || count!=1 ||
                        map.Definition.Nodes.Count(n=>n.Enabled && n.AgvSiteId==node.AgvSiteId)!=1)
                        return Refuse($"第{new[]{1,3,5,7}[step]}步节点{id}缺少唯一真实厂家站点映射。");
                    path.Add(node.AgvSiteId);
                }
                foreach(var pair in path.Zip(path.Skip(1)))
                {
                    if(!checkedPairs.Add(pair)){checks.Add(new(new[]{1,3,5,7}[step],pair.First,pair.Second,true,"本次同批直接路径已观察；整体核验另见总结果。"));continue;}
                    if(simulationLocal)
                    {
                        var sourceNode=map.Definition.Nodes.Single(n=>n.Enabled && n.AgvSiteId==pair.First);
                        var targetNode=map.Definition.Nodes.Single(n=>n.Enabled && n.AgvSiteId==pair.Second);
                        if(!map.Definition.Edges.Any(e=>e.Enabled && e.From==sourceNode.Id && e.To==targetNode.Id))
                        {
                            checks.Add(new(new[]{1,3,5,7}[step],pair.First,pair.Second,false,"隔离仿真同源地图中不存在该有向直接边。"));
                            return Refuse($"第{new[]{1,3,5,7}[step]}步隔离仿真地图路线{pair.First}→{pair.Second}不是有向直接边。");
                        }
                        checks.Add(new(new[]{1,3,5,7}[step],pair.First,pair.Second,true,"车端地图身份已核验；相邻有向边由同一已载入地图版本确认。"));
                        continue;
                    }
                    using var response=await client.RequestAsync(1303,new{source_id=pair.First,target_id=pair.Second},token);
                    if(!response.RootElement.TryGetProperty("list",out var list) || list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()!=2 ||
                        list[0].ValueKind!=JsonValueKind.String || list[1].ValueKind!=JsonValueKind.String ||
                        list[0].GetString()!=pair.First || list[1].GetString()!=pair.Second)
                    {
                        checks.Add(new(new[]{1,3,5,7}[step],pair.First,pair.Second,false,"1303未返回该相邻站点的两点直接路径。"));
                        return Refuse($"第{new[]{1,3,5,7}[step]}步厂家线路{pair.First}→{pair.Second}未返回直接相连路径，禁止跳点。");
                    }
                    checks.Add(new(new[]{1,3,5,7}[step],pair.First,pair.Second,true,"本次观察到1303两点直接路径；整体核验另见总结果。"));
                }
            }
            using var after=await client.RequestAsync(1300,null,token);
            using var loaded=await client.RequestAsync(1022,null,token);
            if(!ReferenceEquals(runtime.Current,map) || !after.RootElement.TryGetProperty("current_map",out var name) || name.GetString()!=binding.RobotMap ||
                !after.RootElement.TryGetProperty("current_map_md5",out var hash) || !string.Equals(hash.GetString(),binding.RobotMapMd5,StringComparison.OrdinalIgnoreCase) ||
                !loaded.RootElement.TryGetProperty("loadmap_status",out var status) || !status.TryGetInt32(out var value) || value!=1)
                return Refuse("路线采集期间地图身份变化或载入未完成。");
            return new(true,simulationLocal
                ? "隔离仿真车端地图身份、明确站点及四段运输的同源有向边核验通过；执行时仍需重新取得路权。"
                : "车端地图绑定、明确站点及四段运输的逐段路径核验通过；执行时仍需重新取得路权。"){RouteChecks=checks.ToArray()};
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){return Refuse(ex is OperationCanceledException?"查询超时，未创建任务。":ex.Message);}
    }
}
