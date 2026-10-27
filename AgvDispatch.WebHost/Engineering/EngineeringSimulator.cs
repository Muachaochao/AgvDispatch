namespace AgvDispatch.WebHost.Engineering;

// Pure reservation simulation: no sockets, PLC writes, pipe clients or command outbox.
// All future resource windows for a job are reserved atomically; this is not the live traffic executor.
public static class EngineeringSimulator
{
    public static readonly string[] Steps = ["前往取货", "确认取走满箱", "前往倒料等待位", "等待倒料机就绪", "进入倒料位", "等待倒料完成", "返回取放箱点", "确认空箱放置", "结束PLC握手"];
    private sealed record Window(string Resource, double Start, double End);
    private sealed record Candidate(EngineeringVehicle Vehicle, string Home, double Start, double End, string[] Route, Window[] Windows);

    public static EngineeringSimulationReport Run(EngineeringVersion version, EngineeringSimulationRequest q)
    {
        var p = version.Plan; var validation = EngineeringValidator.Validate(p);
        if (!validation.Valid) throw new ArgumentException("版本配置校验未通过，不能模拟。");
        if (q.VersionId != version.Id || q.Calls is null || q.Calls.Count is < 1 or > 3000 || q.OfflineVehicles is null || q.BlockedResources is null)
            throw new ArgumentException("请选择版本及1–3000个呼叫。");
        if (q.Calls.Any(x => x is null || string.IsNullOrWhiteSpace(x.RequestId) || x.RequestId.Length > 128 || string.IsNullOrWhiteSpace(x.StationId) || !double.IsFinite(x.AtSeconds) || x.AtSeconds is < 0 or > 86400))
            throw new ArgumentException("呼叫编号、工位或时间无效。");
        var stations = p.Stations.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var resources = p.Resources.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var offline = q.OfflineVehicles.ToHashSet(StringComparer.OrdinalIgnoreCase); var blocked = q.BlockedResources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (offline.Any(id => !p.Vehicles.Any(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) || blocked.Any(id => !resources.ContainsKey(id))) throw new ArgumentException("故障注入引用的车辆或资源不存在。");
        var seen = new Dictionary<string, EngineeringCall>(StringComparer.Ordinal);
        foreach (var call in q.Calls)
        {
            if (seen.TryGetValue(call.RequestId, out var prior) && prior != call) throw new ArgumentException("同一呼叫编号不能变更工位或时间。");
            seen.TryAdd(call.RequestId, call);
        }
        var graph = new EngineeringGraph(p); var positions = p.Vehicles.ToDictionary(v => v.Id, v => v.StartNode, StringComparer.OrdinalIgnoreCase);
        var freeAt = p.Vehicles.ToDictionary(v => v.Id, _ => 0d, StringComparer.OrdinalIgnoreCase);
        var reservations = new List<EngineeringReservation>(); var byResource = new Dictionary<string, List<EngineeringReservation>>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<EngineeringSimTask>(); var failures = new List<EngineeringRejectedCall>();
        var homes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var homeUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in p.Vehicles.Where(v => v.Enabled).OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            var poolIds = p.Pools.Where(x => x.VehicleIds.Contains(v.Id, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bay = p.Standbys.Where(b => poolIds.Contains(b.PoolId) && !blocked.Contains(b.ResourceId) && homeUsage.GetValueOrDefault(b.ResourceId) < resources[b.ResourceId].Capacity && graph.Find(v.StartNode, b.NodeId, blocked) != null)
                .OrderByDescending(b => b.Priority).ThenBy(b => graph.Find(v.StartNode, b.NodeId, blocked)!.Seconds).ThenBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();
            if (bay == null) continue;
            homes[v.Id] = bay.NodeId; homeUsage[bay.ResourceId] = homeUsage.GetValueOrDefault(bay.ResourceId) + 1;
        }
        int Capacity(string resource) => resource.StartsWith("@pickup:", StringComparison.Ordinal) ? 1 : resources[resource].Capacity;
        // Return the first time capacity is exceeded, and the next release at that instant.
        double? Conflict(string resource, double start, double end)
        {
            var existing = byResource.GetValueOrDefault(resource); if (existing == null) return null;
            var touching = existing.Where(r => r.Start < end - 1e-8 && r.End > start + 1e-8).ToArray();
            foreach (var t in touching.Select(x => Math.Max(start, x.Start)).Distinct().Order())
            {
                var active = touching.Where(r => r.Start <= t + 1e-8 && r.End > t + 1e-8).ToArray();
                if (active.Length >= Capacity(resource)) return active.Min(r => r.End);
            }
            return null;
        }
        var pending = seen.Values.ToList(); double decisionTime = 0;
        while (pending.Count > 0)
        {
            // Only arrived calls compete; one priority point per minute prevents indefinite starvation in this finite model.
            decisionTime = Math.Max(decisionTime, pending.Min(c => c.AtSeconds));
            var call = pending.Where(c => c.AtSeconds <= decisionTime).OrderByDescending(c => (stations.GetValueOrDefault(c.StationId)?.Priority ?? 0) + (decisionTime - c.AtSeconds) / 60)
                .ThenBy(c => c.AtSeconds).ThenBy(c => c.RequestId, StringComparer.Ordinal).First(); pending.Remove(call);
            void Fail(string reason, string tab = "stations", string? id = null) => failures.Add(new(call.RequestId, call.StationId, reason, "/engineering?tab=" + tab + "&object=" + Uri.EscapeDataString(id ?? call.StationId)));
            if (!stations.TryGetValue(call.StationId, out var station) || !station.Enabled) { Fail("工位不存在或已停用。"); continue; }
            var pool = p.Pools.Single(x => x.Id.Equals(station.PoolId, StringComparison.OrdinalIgnoreCase)); var template = p.Templates.Single(x => x.Id.Equals(station.TemplateId, StringComparison.OrdinalIgnoreCase));
            var dumper = p.Dumpers.Single(x => x.Id.Equals(station.DumperId, StringComparison.OrdinalIgnoreCase));
            if (blocked.Contains(dumper.DumpingResource) || blocked.Contains(dumper.WaitingResource)) { Fail("倒料区域或等待位已封锁；不分配车辆、不释放故障资源。", "dumpers", dumper.Id); continue; }
            var candidates = new List<Candidate>(); var reasons = new List<string>();
            foreach (var v in p.Vehicles.Where(v => pool.VehicleIds.Contains(v.Id, StringComparer.OrdinalIgnoreCase)))
            {
                if (!v.Enabled || offline.Contains(v.Id)) { reasons.Add(v.Id + "：未启用或模拟离线"); continue; }
                if (v.Battery < v.MinimumBattery) { reasons.Add(v.Id + "：电量低于阈值"); continue; }
                if (v.Capability != template.Capability) { reasons.Add(v.Id + "：车型能力不匹配"); continue; }
                if (!homes.TryGetValue(v.Id, out var home)) { reasons.Add(v.Id + "：没有可分配的待机容量"); continue; }
                var stops = new[] { positions[v.Id], station.PickupNode, dumper.WaitingNode, dumper.DumpingNode, station.PickupNode, home };
                var legs = Enumerable.Range(0, 5).Select(i => graph.Find(stops[i], stops[i + 1], blocked)).ToArray();
                if (legs.Any(x => x == null)) { reasons.Add(v.Id + "：路线被封锁或不可达"); continue; }
                var windows = new List<Window>(); var route = new List<string>(); double t = 0;
                void Leg(int i)
                {
                    var leg = legs[i]!; foreach (var r in leg.Resources) windows.Add(new(r, t, t + leg.Seconds));
                    route.AddRange(leg.Nodes.Skip(route.Count == 0 ? 0 : 1)); t += leg.Seconds;
                }
                Leg(0); var pickupAt = t; t += template.PickupSeconds; windows.Add(new("@pickup:" + station.PickupNode, pickupAt, t));
                Leg(1); var waitAt = t; t += 2; Leg(2); windows.Add(new(dumper.WaitingResource, waitAt, t));
                var dumpAt = t; t += template.DumpSeconds; Leg(3); windows.Add(new(dumper.DumpingResource, dumpAt, t));
                var returnAt = t; t += template.ReturnSeconds + 2; windows.Add(new("@pickup:" + station.PickupNode, returnAt, t)); Leg(4);
                // Merge a job's own overlapping windows for the same resource before capacity calculation.
                var merged = new List<Window>();
                foreach (var group in windows.Where(w => w.End > w.Start).GroupBy(w => w.Resource))
                {
                    Window? current = null;
                    foreach (var w in group.OrderBy(w => w.Start))
                    { if (current != null && w.Start <= current.End) current = current with { End = Math.Max(current.End, w.End) }; else { if (current != null) merged.Add(current); current = w; } }
                    if (current != null) merged.Add(current);
                }
                var start = Math.Max(call.AtSeconds, freeAt[v.Id]); var fits = false;
                for (var attempt = 0; attempt < 20000; attempt++)
                {
                    double next = start;
                    foreach (var w in merged) if (Conflict(w.Resource, start + w.Start, start + w.End) is { } release) next = Math.Max(next, release - w.Start);
                    if (next <= start + 1e-8) { fits = true; break; } start = next;
                }
                if (fits) candidates.Add(new(v, home, start, start + t, route.ToArray(), merged.ToArray()));
                else reasons.Add(v.Id + "：资源预约计算预算耗尽，保留待处理呼叫");
            }
            var chosen = candidates.OrderBy(c => c.Start).ThenBy(c => c.End).ThenByDescending(c => c.Vehicle.Battery).ThenBy(c => c.Vehicle.Id, StringComparer.Ordinal).FirstOrDefault();
            if (chosen == null) { Fail(string.Join("；", reasons), "pools", pool.Id); continue; }
            var taskId = "SIM-TASK-" + (tasks.Count + 1).ToString("D6");
            foreach (var w in chosen.Windows)
            {
                var reservation = new EngineeringReservation(w.Resource, taskId, chosen.Vehicle.Id, chosen.Start + w.Start, chosen.Start + w.End);
                reservations.Add(reservation); if (!byResource.TryGetValue(w.Resource, out var list)) byResource[w.Resource] = list = []; list.Add(reservation);
            }
            tasks.Add(new(taskId, call.RequestId, station.Id, chosen.Vehicle.Id, chosen.Start, chosen.End, chosen.Route, Steps.ToArray()));
            freeAt[chosen.Vehicle.Id] = chosen.End; positions[chosen.Vehicle.Id] = chosen.Home;
            decisionTime = Math.Max(decisionTime, freeAt.Values.Min());
        }
        var peak = 0; var active = 0;
        foreach (var e in tasks.SelectMany(t => new[] { (At: t.Start, Delta: 1), (At: t.End, Delta: -1) }).OrderBy(e => e.At).ThenBy(e => e.Delta)) { active += e.Delta; peak = Math.Max(peak, active); }
        return new(Guid.NewGuid().ToString("N"), version.Id, DateTimeOffset.UtcNow, q.Calls.Count, q.Calls.Count - seen.Count, tasks, failures, reservations, positions,
            tasks.Count == 0 ? 0 : tasks.Max(t => t.End), peak,
            "纯配置与离散预约模拟，无设备通信。九步为模型阶段，PLC反馈以固定耗时建模；未运行实车九步执行器。路线使用配置距离/速度，不做车体碰撞、制动、PLC电气时序、运行中掉线恢复或现场安全验收。未配置交通资源的道路不推断互斥。");
    }
}
