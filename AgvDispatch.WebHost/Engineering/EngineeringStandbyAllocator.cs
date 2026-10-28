namespace AgvDispatch.WebHost.Engineering;

public sealed record EngineeringStandbyAssignment(string VehicleId, string StandbyId, string NodeId, string ResourceId, double TravelSeconds);
public sealed record EngineeringStandbyAllocation(IReadOnlyDictionary<string, EngineeringStandbyAssignment> Assignments,
    IReadOnlyDictionary<string, string> Unassigned);

// Planning-only maximum matching. This neither reserves live resources nor sends navigation.
// Each configured physical node has capacity one, even when its shared resource has capacity > 1.
// Initial parking positions remain reserved to their original vehicle throughout the model.
// No assignment assumes another vehicle has moved or will receive a task.
public static class EngineeringStandbyAllocator
{
    private sealed class Edge(int to, int reverse, int capacity)
    {
        public int To { get; } = to;
        public int Reverse { get; } = reverse;
        public int Capacity { get; set; } = capacity;
    }

    public static EngineeringStandbyAllocation Allocate(EngineeringPlan plan, EngineeringGraph graph,
        HashSet<string> offline, HashSet<string> blocked)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (plan.Standbys.GroupBy(b => b.NodeId, comparer).Any(g => g.Count() > 1))
            throw new ArgumentException("同一物理待机节点不能重复配置或分配给多辆车。");
        if (plan.Vehicles.GroupBy(v => v.StartNode, comparer).Any(g => g.Count() > 1))
            throw new ArgumentException("多辆车初始位置重合；模型不能假定车辆已调位。");
        var resources = plan.Resources.ToDictionary(r => r.Id, comparer);
        var bays = plan.Standbys.OrderByDescending(b => b.Priority).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray();
        var originalOwners = plan.Vehicles.ToDictionary(v => v.StartNode, v => v.Id, comparer);
        // Even online eligible vehicles may never receive a call. Keep every original bay occupied.
        var occupiedCapacity = new Dictionary<string, int>(comparer);
        foreach (var vehicle in plan.Vehicles)
            if (bays.SingleOrDefault(b => comparer.Equals(b.NodeId, vehicle.StartNode)) is { } bay)
                occupiedCapacity[bay.ResourceId] = occupiedCapacity.GetValueOrDefault(bay.ResourceId) + 1;
        if (occupiedCapacity.Any(c => c.Value > resources[c.Key].Capacity))
            throw new ArgumentException("初始停车已超过共享待机资源容量；模型不能假定车辆已离开。");

        var unassigned = new Dictionary<string, string>(comparer);
        var candidates = new Dictionary<string, EngineeringStandbyAssignment[]>(comparer);
        foreach (var vehicle in plan.Vehicles.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            if (!vehicle.Enabled || offline.Contains(vehicle.Id))
            {
                unassigned[vehicle.Id] = "车辆停用或模拟离线，保留原位置，不分配待机导航。";
                continue;
            }
            var pools = plan.Pools.Where(p => p.VehicleIds.Contains(vehicle.Id, comparer)).Select(p => p.Id).ToHashSet(comparer);
            var eligible = bays.Where(b => pools.Contains(b.PoolId) && !blocked.Contains(b.ResourceId)
                    && (!originalOwners.TryGetValue(b.NodeId, out var owner) || comparer.Equals(owner, vehicle.Id))
                    && (comparer.Equals(b.NodeId, vehicle.StartNode) || resources[b.ResourceId].Capacity > occupiedCapacity.GetValueOrDefault(b.ResourceId)))
                .Select(b => (Bay: b, Route: graph.Find(vehicle.StartNode, b.NodeId, blocked)))
                .Where(c => c.Route != null)
                .OrderByDescending(c => c.Bay.Priority).ThenBy(c => c.Route!.Seconds).ThenBy(c => c.Bay.Id, StringComparer.Ordinal)
                .Select(c => new EngineeringStandbyAssignment(vehicle.Id, c.Bay.Id, c.Bay.NodeId, c.Bay.ResourceId, c.Route!.Seconds)).ToArray();
            candidates[vehicle.Id] = eligible;
            if (eligible.Length == 0)
                unassigned[vehicle.Id] = "没有可达且可用的授权待机点：核对车辆池、路线封锁及其他车辆原位占用；模型不自动调位。";
        }

        // source -> vehicle -> physical bay (one) -> shared resource (configured capacity) -> sink.
        // Residual edges can relocate an earlier assignment to find a full matching when greedy choice cannot.
        var fleet = candidates.Where(c => c.Value.Length > 0).OrderBy(c => c.Value.Length).ThenBy(c => c.Key, StringComparer.Ordinal).ToArray();
        var relevantBays = bays.Where(b => candidates.Values.Any(list => list.Any(c => comparer.Equals(c.StandbyId, b.Id)))).ToArray();
        var resourceIds = relevantBays.Select(b => b.ResourceId).Distinct(comparer).Order(StringComparer.Ordinal).ToArray();
        const int source = 0;
        var vehicleNodes = fleet.Select((v, i) => (v.Key, Node: i + 1)).ToDictionary(v => v.Key, v => v.Node, comparer);
        var bayNodes = relevantBays.Select((b, i) => (b.Id, Node: 1 + fleet.Length + i)).ToDictionary(b => b.Id, b => b.Node, comparer);
        var resourceNodes = resourceIds.Select((id, i) => (Id: id, Node: 1 + fleet.Length + relevantBays.Length + i)).ToDictionary(r => r.Id, r => r.Node, comparer);
        var sink = 1 + fleet.Length + relevantBays.Length + resourceIds.Length;
        var network = Enumerable.Range(0, sink + 1).Select(_ => new List<Edge>()).ToArray();
        Edge Add(int from, int to, int capacity)
        {
            var edge = new Edge(to, network[to].Count, capacity);
            network[from].Add(edge); network[to].Add(new(from, network[from].Count - 1, 0)); return edge;
        }
        var choices = new List<(EngineeringStandbyAssignment Assignment, Edge Edge)>();
        foreach (var vehicle in fleet)
        {
            Add(source, vehicleNodes[vehicle.Key], 1);
            foreach (var candidate in vehicle.Value)
                choices.Add((candidate, Add(vehicleNodes[vehicle.Key], bayNodes[candidate.StandbyId], 1)));
        }
        // Returning to one's own initial bay uses capacity already reserved above, not a second slot.
        foreach (var bay in relevantBays)
            Add(bayNodes[bay.Id], originalOwners.ContainsKey(bay.NodeId) ? sink : resourceNodes[bay.ResourceId], 1);
        foreach (var id in resourceIds) Add(resourceNodes[id], sink, Math.Max(0, resources[id].Capacity - occupiedCapacity.GetValueOrDefault(id)));
        while (true)
        {
            var previous = Enumerable.Repeat((-1, -1), network.Length).ToArray();
            var queue = new Queue<int>(); queue.Enqueue(source); previous[source] = (source, -1);
            while (queue.TryDequeue(out var node) && previous[sink].Item1 < 0)
                for (var i = 0; i < network[node].Count; i++)
                {
                    var edge = network[node][i];
                    if (edge.Capacity <= 0 || previous[edge.To].Item1 >= 0) continue;
                    previous[edge.To] = (node, i); queue.Enqueue(edge.To);
                }
            if (previous[sink].Item1 < 0) break;
            for (var node = sink; node != source;)
            {
                var (from, index) = previous[node]; var edge = network[from][index];
                edge.Capacity--; network[node][edge.Reverse].Capacity++; node = from;
            }
        }
        var assignments = choices.Where(c => c.Edge.Capacity == 0).ToDictionary(c => c.Assignment.VehicleId, c => c.Assignment, comparer);
        foreach (var vehicle in fleet.Where(v => !assignments.ContainsKey(v.Key)))
            unassigned[vehicle.Key] = $"待机容量不足：可达授权节点 {string.Join('、', vehicle.Value.Select(c => c.NodeId))} 受物理节点单车容量或共享资源上限限制；保持未分配。";
        return new(assignments, unassigned);
    }
}
