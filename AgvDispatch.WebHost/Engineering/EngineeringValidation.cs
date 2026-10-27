using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgvDispatch.WebHost.Engineering;

public static partial class EngineeringValidator
{
    public static readonly string[] StationInputs = ["FullBoxRequested", "BoxPresent", "Faulted", "CommunicationOnline"];
    public static readonly string[] StationOutputs = ["FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced"];
    public static readonly string[] DumperInputs = ["DumperReady", "DumpingCompletedMayLeave", "Faulted"];
    public static readonly string[] DumperOutputs = ["AgvAtWaitingPosition", "AgvAtDumpingPosition"];
    [GeneratedRegex(@"^DB([1-9][0-9]{0,4})\.DBX([0-9]{1,6})\.([0-7])$", RegexOptions.IgnoreCase)]
    private static partial Regex BitAddress();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Identifier();

    public static void CheckShape(EngineeringPlan p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var json = JsonSerializer.SerializeToElement(p, WebJson.Options);
        if (json.GetRawText().Length > 4 * 1024 * 1024 || p.SchemaVersion != 1 || string.IsNullOrWhiteSpace(p.Name))
            throw new ArgumentException("工程格式、名称或大小无效（上限4MiB）。");
        void Check(JsonElement item)
        {
            if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new ArgumentException("工程字段不能为null。");
            if (item.ValueKind == JsonValueKind.String && item.GetString()!.Length > 1024) throw new ArgumentException("工程文本过长。");
            if (item.ValueKind == JsonValueKind.Object) foreach (var v in item.EnumerateObject()) Check(v.Value);
            if (item.ValueKind == JsonValueKind.Array) foreach (var v in item.EnumerateArray()) Check(v);
        }
        Check(json);
        foreach (var key in new[] { "nodes", "edges", "resources", "plcs", "signals", "vehicles", "pools", "stations", "templates", "dumpers", "standbys" })
        {
            var max = key == "signals" ? 12000 : key == "edges" ? 10000 : key == "nodes" ? 5000 : key == "stations" ? 1000 : key == "resources" ? 2000 : 100;
            if (json.GetProperty(key).GetArrayLength() > max) throw new ArgumentException($"{key}超过配置数量预算{max}。");
        }
    }

    public static EngineeringValidation Validate(EngineeringPlan p)
    {
        CheckShape(p);
        var issues = new List<EngineeringIssue>();
        void Error(string section, string id, string field, string message) => issues.Add(new("Error", section, id, field, message));
        void Warn(string section, string id, string field, string message) => issues.Add(new("Warning", section, id, field, message));
        Dictionary<string, T> Index<T>(IEnumerable<T> rows, Func<T, string> key, string section)
        {
            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var id = key(row);
                if (!Identifier().IsMatch(id)) Error(section, id, "id", "编号仅支持1–64位字母、数字、下划线、连字符。");
                if (!result.TryAdd(id, row)) Error(section, id, "id", "编号重复（不区分大小写）。");
            }
            return result;
        }
        var nodes = Index(p.Nodes, x => x.Id, "nodes"); var resources = Index(p.Resources, x => x.Id, "resources");
        var plcs = Index(p.Plcs, x => x.Id, "plcs"); var vehicles = Index(p.Vehicles, x => x.Id, "vehicles");
        var pools = Index(p.Pools, x => x.Id, "pools"); var templates = Index(p.Templates, x => x.Id, "templates");
        var dumpers = Index(p.Dumpers, x => x.Id, "dumpers"); var stations = Index(p.Stations, x => x.Id, "stations");
        Index(p.Standbys, x => x.Id, "standbys"); Index(p.Signals, x => x.Id, "signals"); Index(p.Edges, x => x.Id, "edges");
        if (string.IsNullOrWhiteSpace(p.MapId) || string.IsNullOrWhiteSpace(p.MapVersion)) Error("overview", p.Name, "mapVersion", "请绑定地图编号和版本。");
        if (p.Stations.Count == 0 || p.Vehicles.Count == 0) Error("stations", p.Name, "id", "工程至少需要一个工位和一台车辆。");
        foreach (var n in p.Nodes)
            if (string.IsNullOrWhiteSpace(n.VendorSite) || !double.IsFinite(n.X) || !double.IsFinite(n.Y) || Math.Abs(n.X) > 1e7 || Math.Abs(n.Y) > 1e7)
                Error("nodes", n.Id, "vendorSite", "厂家站点或米制坐标无效。");
        foreach (var g in p.Nodes.GroupBy(x => x.VendorSite, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error("nodes", g.First().Id, "vendorSite", "多个业务节点绑定同一厂家站点，请消除歧义。");
        foreach (var r in p.Resources)
            if (r.Capacity is < 1 or > 100 || !new[] { "Intersection", "NarrowAisle", "Station", "Waiting", "Dumper", "Standby" }.Contains(r.Kind))
                Error("resources", r.Id, "capacity", "资源类型或容量无效（1–100）。展示区域请留在地图场景编辑，不作为资源导入。");
        void Node(string section, string id, string field, string node)
        { if (!nodes.TryGetValue(node, out var n) || !n.Enabled) Error(section, id, field, $"节点 {node} 不存在或未启用。"); }
        void Resource(string section, string id, string field, string resource)
        { if (!resources.ContainsKey(resource)) Error(section, id, field, $"交通资源 {resource} 不存在。"); }
        void Plc(string section, string id, string plc)
        { if (!plcs.TryGetValue(plc, out var item) || !item.Enabled) Error(section, id, "plcId", $"PLC {plc} 不存在或未启用。"); }
        foreach (var e in p.Edges)
        {
            Node("edges", e.Id, "from", e.From); Node("edges", e.Id, "to", e.To);
            if (e.From.Equals(e.To, StringComparison.OrdinalIgnoreCase) || !double.IsFinite(e.Distance) || e.Distance <= 0 || e.Distance > 100000 || !double.IsFinite(e.Speed) || e.Speed <= 0 || e.Speed > 10)
                Error("edges", e.Id, "distance", "道路端点、米制距离或速度无效。");
            foreach (var r in e.ResourceIds) Resource("edges", e.Id, "resourceIds", r);
        }
        foreach (var plc in p.Plcs)
            if (string.IsNullOrWhiteSpace(plc.Host) || plc.Port is < 1 or > 65535) Error("plcs", plc.Id, "host", "PLC地址或端口无效。");
        var bitOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var semanticOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in p.Signals)
        {
            Plc("signals", s.Id, s.PlcId);
            if (!stations.ContainsKey(s.OwnerId) && !dumpers.ContainsKey(s.OwnerId)) Error("signals", s.Id, "ownerId", "信号未绑定已配置工位或倒料设备。");
            var match = BitAddress().Match(s.Address);
            if (!match.Success || s.DataType != "Bool" || s.Direction is not ("Read" or "Write")) Error("signals", s.Id, "address", "当前握手模板需要Bool、DBn.DBX字节.位地址及Read/Write方向。");
            else
            {
                var normalized = s.PlcId + ":" + string.Join(":", match.Groups.Values.Skip(1).Select(x => int.Parse(x.Value)));
                if (!bitOwners.TryAdd(normalized, s.Id)) Error("signals", s.Id, "address", $"PLC地址与 {bitOwners[normalized]} 冲突。");
            }
            if (!semanticOwners.Add(s.OwnerId + ":" + s.Name)) Error("signals", s.Id, "name", "同一设备的信号含义重复。");
        }
        void Signals(string section, string id, string plcId, string[] input, string[] output)
        {
            foreach (var (name, direction) in input.Select(x => (x, "Read")).Concat(output.Select(x => (x, "Write"))))
                if (!p.Signals.Any(s => s.OwnerId.Equals(id, StringComparison.OrdinalIgnoreCase) && s.PlcId.Equals(plcId, StringComparison.OrdinalIgnoreCase) && s.Name == name && s.Direction == direction))
                    Error(section, id, "plcId", $"缺少PLC信号 {name}（{direction}），请在点表配置中补齐。");
        }
        foreach (var v in p.Vehicles)
        {
            Node("vehicles", v.Id, "startNode", v.StartNode);
            if (string.IsNullOrWhiteSpace(v.Host) || string.IsNullOrWhiteSpace(v.Capability) || !double.IsFinite(v.Radius) || v.Radius <= 0 || v.Radius > 10 || !double.IsFinite(v.LocalizationError) || v.LocalizationError < 0 || v.LocalizationError > 5 || string.IsNullOrWhiteSpace(v.EnvelopeEvidence))
                Error("vehicles", v.Id, "radius", "请配置车辆能力、包络半径、定位误差及依据。模拟示例数值不作为现场核定。");
            if (!double.IsFinite(v.Battery) || !double.IsFinite(v.MinimumBattery) || v.Battery is < 0 or > 100 || v.MinimumBattery is < 0 or > 100)
                Error("vehicles", v.Id, "battery", "电量和派车阈值必须在0–100之间。");
        }
        foreach (var t in p.Templates)
            if (string.IsNullOrWhiteSpace(t.Capability) || new[] { t.PickupSeconds, t.DumpSeconds, t.ReturnSeconds }.Any(x => !double.IsFinite(x) || x <= 0 || x > 3600))
                Error("templates", t.Id, "capability", "任务模板能力或模拟动作耗时无效。");
        foreach (var pool in p.Pools)
        {
            if (pool.VehicleIds.Length == 0 || pool.VehicleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.VehicleIds.Length) Error("pools", pool.Id, "vehicleIds", "车辆池不能为空或重复引用车辆。");
            foreach (var id in pool.VehicleIds) if (!vehicles.ContainsKey(id)) Error("pools", pool.Id, "vehicleIds", $"车辆 {id} 不存在。");
        }
        foreach (var d in p.Dumpers)
        {
            Plc("dumpers", d.Id, d.PlcId); Node("dumpers", d.Id, "waitingNode", d.WaitingNode); Node("dumpers", d.Id, "dumpingNode", d.DumpingNode);
            Resource("dumpers", d.Id, "waitingResource", d.WaitingResource); Resource("dumpers", d.Id, "dumpingResource", d.DumpingResource);
            if (d.WaitingNode == d.DumpingNode || d.WaitingResource == d.DumpingResource) Error("dumpers", d.Id, "waitingNode", "等待位与倒料位必须分开绑定。");
            Signals("dumpers", d.Id, d.PlcId, DumperInputs, DumperOutputs);
        }
        foreach (var b in p.Standbys)
        {
            Node("standbys", b.Id, "nodeId", b.NodeId); Resource("standbys", b.Id, "resourceId", b.ResourceId);
            if (!pools.ContainsKey(b.PoolId)) Error("standbys", b.Id, "poolId", "待机位车辆池不存在。");
            if (b.Priority is < 0 or > 100) Error("standbys", b.Id, "priority", "优先级应在0–100之间。");
            if (p.Dumpers.Any(d => d.WaitingNode.Equals(b.NodeId, StringComparison.OrdinalIgnoreCase) || d.DumpingNode.Equals(b.NodeId, StringComparison.OrdinalIgnoreCase)))
                Error("standbys", b.Id, "nodeId", "待机位不能与倒料等待位或倒料位共用物理节点。");
            if (resources.GetValueOrDefault(b.ResourceId)?.Kind != "Standby") Error("standbys", b.Id, "resourceId", "待机位必须绑定Standby类型资源。");
        }
        foreach (var g in p.Standbys.GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error("standbys", g.First().Id, "nodeId", "同一物理待机点重复配置；请用一个资源容量表达。");
        foreach (var pool in p.Pools)
        {
            var capacity = p.Standbys.Where(b => b.PoolId == pool.Id).Select(b => b.ResourceId).Distinct().Sum(id => resources.GetValueOrDefault(id)?.Capacity ?? 0);
            if (capacity < pool.VehicleIds.Count(id => vehicles.GetValueOrDefault(id)?.Enabled == true)) Error("pools", pool.Id, "vehicleIds", "待机位总容量小于已启用车辆数。");
        }
        var graph = new EngineeringGraph(p);
        foreach (var s in p.Stations.Where(s => s.Enabled))
        {
            Node("stations", s.Id, "pickupNode", s.PickupNode); Plc("stations", s.Id, s.PlcId);
            Signals("stations", s.Id, s.PlcId, StationInputs, StationOutputs);
            if (!templates.TryGetValue(s.TemplateId, out var template)) Error("stations", s.Id, "templateId", "任务模板不存在。");
            if (!pools.TryGetValue(s.PoolId, out var pool)) Error("stations", s.Id, "poolId", "车辆池不存在。");
            if (!dumpers.TryGetValue(s.DumperId, out var dumper)) Error("stations", s.Id, "dumperId", "倒料设备不存在。");
            if (s.Priority is < 0 or > 100) Error("stations", s.Id, "priority", "优先级应在0–100之间。");
            if (pool != null && template != null && !pool.VehicleIds.Any(id => vehicles.GetValueOrDefault(id) is { Enabled: true } v && v.Capability == template.Capability))
                Error("stations", s.Id, "poolId", "车辆池没有具备模板所需能力的启用车辆。");
            if (dumper != null && (graph.Find(s.PickupNode, dumper.WaitingNode) == null || graph.Find(dumper.WaitingNode, dumper.DumpingNode) == null || graph.Find(dumper.DumpingNode, s.PickupNode) == null))
                Error("stations", s.Id, "pickupNode", "取货→等待→倒料→返箱路线不完整；检查道路方向与启用状态。");
            if (pool != null && !p.Standbys.Where(b => b.PoolId == pool.Id).Any(b => graph.Find(b.NodeId, s.PickupNode) != null && graph.Find(s.PickupNode, b.NodeId) != null))
                Error("stations", s.Id, "poolId", "工位与所属车辆池待机位之间无可往返路线。");
        }
        foreach (var g in p.Stations.Where(s => s.Enabled).GroupBy(s => s.PickupNode).Where(g => g.Count() > 1))
            Warn("stations", g.First().Id, "pickupNode", "多个工位共用取放点；模拟按取放点互斥，现场仍需核定物理容量。");
        return new(issues);
    }
}

public sealed record EngineeringRoute(string[] Nodes, string[] Resources, double Seconds);
public sealed class EngineeringGraph
{
    private readonly Dictionary<string, List<(string To, double Seconds, string[] Resources)>> adjacency = new(StringComparer.OrdinalIgnoreCase);
    public EngineeringGraph(EngineeringPlan p)
    {
        foreach (var n in p.Nodes.Where(n => n.Enabled)) adjacency.TryAdd(n.Id, []);
        foreach (var e in p.Edges.Where(e => e.Enabled && e.Distance > 0 && e.Speed > 0 && double.IsFinite(e.Distance / e.Speed)))
        {
            if (!adjacency.ContainsKey(e.From) || !adjacency.ContainsKey(e.To)) continue;
            adjacency[e.From].Add((e.To, e.Distance / e.Speed, e.ResourceIds));
            if (e.Bidirectional) adjacency[e.To].Add((e.From, e.Distance / e.Speed, e.ResourceIds));
        }
    }
    public EngineeringRoute? Find(string from, string to, HashSet<string>? blocked = null)
    {
        if (!adjacency.ContainsKey(from) || !adjacency.ContainsKey(to)) return null;
        var distance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [from] = 0 };
        var previous = new Dictionary<string, (string From, string[] Resources)>(StringComparer.OrdinalIgnoreCase);
        var queue = new PriorityQueue<string, double>(); queue.Enqueue(from, 0);
        while (queue.TryDequeue(out var node, out var cost))
        {
            if (cost > distance[node]) continue;
            if (node.Equals(to, StringComparison.OrdinalIgnoreCase)) break;
            foreach (var e in adjacency[node])
            {
                if (blocked != null && e.Resources.Any(blocked.Contains)) continue;
                var next = cost + e.Seconds;
                if (distance.TryGetValue(e.To, out var old) && next >= old) continue;
                distance[e.To] = next; previous[e.To] = (node, e.Resources); queue.Enqueue(e.To, next);
            }
        }
        if (!distance.TryGetValue(to, out var total)) return null;
        var nodes = new List<string> { to }; var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var current = to;
        while (!current.Equals(from, StringComparison.OrdinalIgnoreCase)) { var p = previous[current]; nodes.Add(p.From); resources.UnionWith(p.Resources); current = p.From; }
        nodes.Reverse(); return new(nodes.ToArray(), resources.Order().ToArray(), total);
    }
}
