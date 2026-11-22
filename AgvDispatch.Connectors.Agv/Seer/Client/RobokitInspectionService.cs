using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Connectors.Agv;

/// <summary>Explicit read-only inspection; does not acquire control or change maps.</summary>
public sealed class RobokitInspectionService(AgvOptions options, AgvConnectionManager connections,
    IActiveMapRuntime maps) : IRobokitInspectionService
{
    public async Task<RobokitInspectionSnapshot> ReadAsync(RobokitInspectionQuery query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.VehicleId);
        var local = maps.Current;
        RobokitInspectionSnapshot Unavailable(string reason) => new(query.VehicleId, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),
            false, false, reason, null, null, null, null, null, local?.MapId, local?.VersionId,
            null, null, [], [], [], [reason]);
        if (!options.Enabled) return Unavailable("AGV采集未启用；未连接设备。");
        if (options.GetConfigurationError() is { } error) return Unavailable(error);
        if (!connections.TryGet(query.VehicleId, out var client)) return Unavailable("未配置或未启用该车辆。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        try
        {
            using var info = await client.RequestAsync(1000, null, token);
            using var map = await client.RequestAsync(1300, null, token);
            using var load = await client.RequestAsync(1022, null, token);
            using var owner = await client.RequestAsync(1060, null, token);
            using var stationsResponse = await client.RequestAsync(1301, null, token);
            var name = String(map.RootElement, "current_map");
            var md5 = String(map.RootElement, "current_map_md5");
            var issues = new List<string>();
            var stations = new List<RobokitStation>();
            if (!stationsResponse.RootElement.TryGetProperty("stations", out var list) || list.ValueKind != JsonValueKind.Array)
                issues.Add("1301缺少有效stations数组。");
            else
            {
                if (list.GetArrayLength() > 10000) throw new InvalidDataException("站点数量超过10000项预算。");
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { issues.Add("站点记录不是对象。"); continue; }
                    var id = String(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) { issues.Add("站点记录缺少id。"); continue; }
                    stations.Add(new(id, String(item,"type"), Number(item,"x"), Number(item,"y"), Number(item,"r"), String(item,"desc")));
                }
            }
            var duplicateSites = stations.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count()>1)
                .Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
            if (duplicateSites.Count > 0) issues.Add("车端返回重复站点编号，不能据此核验映射。");
            var siteIds = stations.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);
            var mappings = new List<RobokitNodeMapping>();
            var nodes = local?.Definition.Nodes;
            var duplicateMappings = nodes?.Where(n=>!string.IsNullOrWhiteSpace(n.AgvSiteId))
                .GroupBy(n=>n.AgvSiteId!, StringComparer.Ordinal).Where(g=>g.Count()>1)
                .Select(g=>g.Key).ToHashSet(StringComparer.Ordinal) ?? [];
            if (nodes is not null)
                foreach (var node in nodes)
                {
                    var site = node.AgvSiteId;
                    var found = !string.IsNullOrWhiteSpace(site) && siteIds.Contains(site) &&
                        !duplicateSites.Contains(site) && !duplicateMappings.Contains(site);
                    mappings.Add(new(node.Id, site, found, found ? "车端存在此站点；未核定坐标转换及地图关联" :
                        string.IsNullOrWhiteSpace(site) ? "缺少明确AgvSiteId，不用逻辑编号自动代替" : "站点不存在或映射重复"));
                }
            else issues.Add("后台没有活动地图，不能核对业务节点。");
            if (mappings.Any(m=>!m.Found)) issues.Add("存在缺失或不唯一的业务节点映射。");
            var route = new List<string>();
            var sourcePresent = !string.IsNullOrWhiteSpace(query.SourceNodeId);
            var targetPresent = !string.IsNullOrWhiteSpace(query.TargetNodeId);
            if (sourcePresent != targetPresent) issues.Add("路径查询须同时填写起点和终点逻辑编号。");
            if (sourcePresent && targetPresent)
            {
                var source = mappings.SingleOrDefault(m=>m.NodeId == query.SourceNodeId && m.Found);
                var target = mappings.SingleOrDefault(m=>m.NodeId == query.TargetNodeId && m.Found);
                if (source is null || target is null) issues.Add("路径端点没有有效且明确的厂家站点映射，未查询1303。");
                else
                {
                    using var path = await client.RequestAsync(1303,
                        new { source_id = source.SiteId, target_id = target.SiteId }, token);
                    if (!path.RootElement.TryGetProperty("list", out var pathList) || pathList.ValueKind != JsonValueKind.Array ||
                        pathList.GetArrayLength()>2048) issues.Add("1303路径列表缺失、类型错误或超过预算。");
                    else
                    {
                        foreach(var site in pathList.EnumerateArray())
                            if(site.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(site.GetString())) route.Add(site.GetString()!);
                            else issues.Add("1303路径含无效站点。");
                        if(route.Count == 0 || route[0] != source.SiteId || route[^1] != target.SiteId || route.Any(s=>!siteIds.Contains(s)))
                            issues.Add("1303路径端点或途经站点未通过核对。");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var file = name.EndsWith(".smap", StringComparison.Ordinal) ? name : name + ".smap";
                using var hashes = await client.RequestAsync(1302, new { map_names = new[] { file } }, token);
                var matched = hashes.RootElement.TryGetProperty("map_info", out var hashList) && hashList.ValueKind == JsonValueKind.Array &&
                    hashList.EnumerateArray().Any(h=>String(h,"name")==file && String(h,"md5")==md5);
                if (!matched) issues.Add("1302地图MD5未与1300当前地图一致；地图文件命名及固件返回需核对。");
            }
            using var after = await client.RequestAsync(1300, null, token);
            using var afterLoad = await client.RequestAsync(1022, null, token);
            var loadStatus = Integer(load.RootElement,"loadmap_status");
            var stable = !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(md5) &&
                name == String(after.RootElement,"current_map") && md5 == String(after.RootElement,"current_map_md5") &&
                loadStatus == 1 && Integer(afterLoad.RootElement,"loadmap_status") == 1 && ReferenceEquals(local,maps.Current);
            if(!stable) issues.Add("采集期间地图/版本变化、载入未完成或地图身份缺失，整批不可作核验依据。");
            if(String(info.RootElement,"version") is null) issues.Add("1000缺少Robokit版本。");
            issues.Add("只读观察：未核定本地地图与车端MD5关联、坐标转换或完整线路，不作为调度放行条件。");
            return new(query.VehicleId, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),true,stable,
                stable ? "只读采集完成；请查看核验提示" : "采集完成但地图不稳定", String(info.RootElement,"version"),
                String(info.RootElement,"netprotocol_version"),name,md5,loadStatus,local?.MapId,local?.VersionId,
                Boolean(owner.RootElement,"locked"),String(owner.RootElement,"nick_name"),stations,mappings,route,issues);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception ex) { return Unavailable(ex is OperationCanceledException ? "只读采集超时，不保留上次健康结果。" : "只读采集失败："+ex.Message); }
    }
    private static string? String(JsonElement value,string key) => value.ValueKind==JsonValueKind.Object && value.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.String ? p.GetString() : null;
    private static double? Number(JsonElement value,string key) => value.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static int? Integer(JsonElement value,string key) => value.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;
    private static bool? Boolean(JsonElement value,string key) => value.TryGetProperty(key,out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
}
