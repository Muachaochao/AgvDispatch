using System.Collections.Concurrent;
using System.Text;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;

namespace AgvDispatch.Service;

/// <summary>Bind commissioning detours to the closed runtime topology and a fresh
/// read-only robot map. Shared LM station names alone do not identify a map.</summary>
public sealed class NavigationTestDetourPlanner(IActiveMapRuntime runtime, IRoadClosureService closures,
    IRobotMapDownloadService downloads) : INavigationTestDetourPlanner
{
    private readonly ConcurrentDictionary<string, byte> _verified = new(StringComparer.Ordinal);

    public async Task<NavigationTestDetourPlan> PreviewAsync(NavigationTestDetourPlanRequest request, CancellationToken ct)
    {
        var map = runtime.Current;
        var snapshot = await closures.ListAsync(ct);
        NavigationTestDetourPlan Refuse(string message) => new(false, message, snapshot.Revision,
            snapshot.MapId ?? "", snapshot.VersionId ?? "", snapshot.DefinitionHash ?? "",
            request.RobotMap, request.RobotMapMd5, request.SourceSite, request.TargetSite, []);
        if (map is null || !snapshot.Ready || snapshot.IdentityConflict || snapshot.MapId != map.MapId ||
            snapshot.VersionId != map.VersionId ||
            !string.Equals(snapshot.DefinitionHash, EngineeringRuntimeIdentity.MapHash(map.Definition), StringComparison.OrdinalIgnoreCase) ||
            map.Definition.CoordinateSystem != "RobotWorld" || map.Definition.Unit != "m")
            return Refuse("当前运行地图或道路封锁状态不可核对，不能生成可执行改道。");
        if (request.Stations.Count == 0 || request.Stations.GroupBy(s => s.Id, StringComparer.Ordinal).Any(g => g.Count() != 1))
            return Refuse("车端站点证据缺失或重复。");
        var blocked=snapshot.BlockedTraversals.ToList();
        foreach(var pair in request.SuggestedBlockedPairs.Distinct())
        {
            var from=MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,pair.FromSite);
            var to=MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,pair.ToSite);
            blocked.AddRange(map.Definition.Edges.Where(edge=>edge.Enabled&&(
                string.Equals(edge.From,from,StringComparison.OrdinalIgnoreCase)&&string.Equals(edge.To,to,StringComparison.OrdinalIgnoreCase)||
                edge.Bidirectional&&string.Equals(edge.From,to,StringComparison.OrdinalIgnoreCase)&&string.Equals(edge.To,from,StringComparison.OrdinalIgnoreCase)))
                .Select(edge=>new MapDirectedTraversal(edge.Id,
                    edge.Bidirectional&&string.Equals(edge.From,to,StringComparison.OrdinalIgnoreCase)?to:from,
                    edge.Bidirectional&&string.Equals(edge.From,to,StringComparison.OrdinalIgnoreCase)?from:to)));
        }
        blocked=blocked.Distinct().ToList();
        if (blocked.Count == 0)
            return Refuse("无法从当前原路线唯一推断受阻道路；请核对车辆站点后重试，或由现场明确封锁通道。");
        try
        {
            // The independent forklift test need not have a production business-map
            // binding. Compare its current 4011 export in robot coordinates instead.
            var downloaded = await downloads.ReadCurrentAsync(new(request.VehicleId), ct);
            if (downloaded.VehicleId != request.VehicleId || downloaded.MapName != request.RobotMap ||
                !string.Equals(downloaded.RobotMapMd5, request.RobotMapMd5, StringComparison.OrdinalIgnoreCase))
                return Refuse("车端地图名称或MD5在核验期间变化，请重新预览。");
            var imported = new Smap2DImportService().Read(Encoding.UTF8.GetBytes(downloaded.JsonContent), "detour:4011");
            if (imported.OriginalMapName != request.RobotMap)
                return Refuse("当前车端导出地图身份不一致。");
            var robotMap = imported.Definition with
            {
                Nodes = imported.Definition.Nodes.Select(n => n with { X = n.X + imported.OffsetX, Y = n.Y + imported.OffsetY }).ToArray(),
                Edges = imported.Definition.Edges.Select(e => e with
                { ControlPoints = e.ControlPoints?.Select(p => new MapPoint2D(p.X + imported.OffsetX, p.Y + imported.OffsetY)).ToArray() }).ToArray()
            };
            if (!TopologyMatches(map.Definition, robotMap, request.Stations))
                return Refuse("运行图的站点坐标或有向道路与当前车端导出不一致，请核对测试地图后再改道。");
            var sourceNode=MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,request.SourceSite);
            var targetNode=MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,request.TargetSite);
            var route=map.Graph.FindShortestTransportRoute(sourceNode,targetNode,new HashSet<string>(),null,null,blocked);
            if(route is null)return Refuse("自动推断受阻道路后没有可用备选路线，保持原任务等待。");
            var result=MapRouteSiteMapper.ToAgvSitePlan(map.Definition,route);
            var plan = new NavigationTestDetourPlan(true, "已按当前车端地图核对候选站点链；尚未取消或发送导航。",
                snapshot.Revision, map.MapId, map.VersionId, snapshot.DefinitionHash!, request.RobotMap,
                request.RobotMapMd5, request.SourceSite, request.TargetSite, result.Nodes.ToArray())
            { BlockedEdgeIds = blocked.Select(t => t.EdgeId).Distinct().ToArray() };
            if (plan.Route.Count < 2 || plan.Route[0] != request.SourceSite || plan.Route[^1] != request.TargetSite)
                return Refuse("候选路线未映射到准确的厂家起终点。");
            if (!ReferenceEquals(map, runtime.Current) || closures.GetSnapshot().Revision != snapshot.Revision)
                return Refuse("核验期间地图或封锁发生变化，请重新预览。");
            _verified[Key(plan)] = 0;
            return plan;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Refuse("候选地图核验失败：" + ex.Message); }
    }

    public Task ValidateAsync(NavigationTestDetourPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = closures.GetSnapshot();
        if (!plan.Available || !_verified.ContainsKey(Key(plan)) || !snapshot.Ready || snapshot.IdentityConflict ||
            snapshot.MapId != plan.MapId || snapshot.VersionId != plan.VersionId ||
            !string.Equals(snapshot.DefinitionHash, plan.DefinitionHash, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Revision != plan.ClosureRevision)
            throw new InvalidOperationException("改道核验已失效：地图、封锁版本变化或后台重启，请保留原任务并重新预览。");
        var map=runtime.Current??throw new InvalidOperationException("运行地图已移除，无法核验改道。");
        var blocked=plan.BlockedEdgeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(plan.Route.Zip(plan.Route.Skip(1)).Any(pair=>map.Definition.Edges.Any(edge=>
            blocked.Contains(edge.Id)&&
            (string.Equals(edge.From,MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,pair.First),StringComparison.OrdinalIgnoreCase)&&
             string.Equals(edge.To,MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,pair.Second),StringComparison.OrdinalIgnoreCase)||
             edge.Bidirectional&&string.Equals(edge.From,MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,pair.Second),StringComparison.OrdinalIgnoreCase)&&
             string.Equals(edge.To,MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,pair.First),StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidOperationException("候选路线仍包含自动推断的受阻道路，禁止发送。");
        return Task.CompletedTask;
    }

    public async ValueTask<IDisposable> EnterSendAsync(NavigationTestDetourPlan plan, CancellationToken ct)
    {
        var lease = await closures.EnterRouteSendAsync(plan.Route, ct);
        try { await ValidateAsync(plan, ct); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private static string Key(NavigationTestDetourPlan plan) => System.Text.Json.JsonSerializer.Serialize(plan);
    private static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= 0.00001;
    private static bool TopologyMatches(MapDefinition local, MapDefinition robot, IReadOnlyList<NavigationTestStation> stations)
    {
        string? Site(MapDefinition map, string id) => map.Nodes.Single(n => n.Id == id).AgvSiteId;
        foreach (var node in local.Nodes.Where(n => n.Enabled))
        {
            var remote = robot.Nodes.Where(n => n.AgvSiteId == node.AgvSiteId).ToArray();
            var observed = stations.Where(n => n.Id == node.AgvSiteId).ToArray();
            if (string.IsNullOrWhiteSpace(node.AgvSiteId) || remote.Length != 1 || observed.Length != 1 ||
                !Near(node.X, remote[0].X) || !Near(node.Y, remote[0].Y) ||
                !Near(node.X, observed[0].X) || !Near(node.Y, observed[0].Y)) return false;
        }
        bool GeometryMatches(MapEdge edge, MapEdge other, bool reverse)
        {
            if (edge.GeometryKind != other.GeometryKind) return false;
            var a = edge.ControlPoints ?? [];
            var b = reverse ? (other.ControlPoints ?? []).Reverse().ToArray() : other.ControlPoints ?? [];
            return a.Count == b.Count && a.Zip(b).All(pair => Near(pair.First.X, pair.Second.X) && Near(pair.First.Y, pair.Second.Y));
        }
        foreach (var edge in local.Edges.Where(e => e.Enabled))
        {
            var from = Site(local, edge.From); var to = Site(local, edge.To);
            if (!robot.Edges.Any(e => Site(robot, e.From) == from && Site(robot, e.To) == to && GeometryMatches(edge, e, false) ||
                e.Bidirectional && Site(robot, e.To) == from && Site(robot, e.From) == to && GeometryMatches(edge, e, true))) return false;
            if (edge.Bidirectional && !robot.Edges.Any(e => Site(robot, e.From) == to && Site(robot, e.To) == from && GeometryMatches(edge, e, true) ||
                e.Bidirectional && Site(robot, e.From) == from && Site(robot, e.To) == to && GeometryMatches(edge, e, false))) return false;
        }
        return true;
    }
}
