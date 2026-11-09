using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

/// <summary>
/// Read-only, task-level resource wait analysis. A cycle is a logical wait risk, never a
/// physical deadlock certificate or a command to release a lease. In particular, expired
/// heartbeats remain owners. Requests and leases must come from one database snapshot.
/// </summary>
internal static class TrafficCycleRiskDetector
{
    public static IReadOnlyList<TrafficCycleRisk> Find(IReadOnlyList<TrafficRequestEntity> requests,
        IReadOnlyDictionary<string, DispatchTaskEntity> tasks, IReadOnlyDictionary<string, DispatchTaskStepEntity> steps,
        IReadOnlyList<TrafficResourceLeaseEntity> leases, TrafficResourceCatalog catalog, DateTime now)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var waiting = requests.Where(r => r.State == "Waiting" && (r.WaitingCategory is null or "Route") &&
            !string.IsNullOrWhiteSpace(r.VehicleId) && tasks.TryGetValue(r.TaskId, out var task) &&
            Enum.IsDefined(typeof(DispatchTaskStage), task.Stage) && task.Stage is >= 0 and < 90 && comparer.Equals(task.VehicleId, r.VehicleId) &&
            steps.TryGetValue(r.StepId, out var step) && comparer.Equals(step.TaskId, r.TaskId) &&
            step.Status is 0 or 10 or 40 or 60).ToArray();
        // Conflicting vehicle identities cannot establish a cycle. Retained leases still
        // remain visible through the normal waiting/ownership queries.
        var byTask = waiting.GroupBy(r => r.TaskId, comparer)
            .Where(g => g.Select(r => r.VehicleId).Distinct(comparer).Count() == 1 &&
                leases.Where(l => comparer.Equals(l.TaskId, g.Key)).All(l => comparer.Equals(l.VehicleId, g.First().VehicleId)))
            .ToDictionary(g => g.Key, g => g.ToArray(), comparer);
        var demands = byTask.ToDictionary(g => g.Key, g => g.Value.SelectMany(r => ReadResources(r.ResourceIds))
            .Distinct(comparer).ToArray(), comparer);
        var owners = leases.GroupBy(l => l.ResourceId, comparer).ToDictionary(g => g.Key,
            g => g.Select(l => l.TaskId).Distinct(comparer).ToArray(), comparer);
        var capacities = catalog.All.ToDictionary(r => r.Id, r => r.Capacity, comparer);

        // Resource-capacity reduction: a cycle through one of several owners is not a
        // closed wait if another owner can finish and free a slot. This is essential for
        // capacity > 1; ordinary graph cycles alone produce misleading deadlock alarms.
        var remaining = byTask.Keys.ToHashSet(comparer);
        bool Saturated(string task, string resource) => capacities.TryGetValue(resource, out var capacity) &&
            capacity > 0 && owners.TryGetValue(resource, out var holders) &&
            holders.Count(id => !comparer.Equals(id, task) && remaining.Contains(id)) >= capacity;
        while (true)
        {
            var reducible = remaining.Where(task => !demands[task].Any(resource => Saturated(task, resource))).ToArray();
            if (reducible.Length == 0) break;
            foreach (var task in reducible) remaining.Remove(task);
        }
        if (remaining.Count < 2) return [];

        var links = new List<TrafficCycleRiskLink>();
        foreach (var task in remaining.Order(comparer))
        foreach (var request in byTask[task].OrderBy(r => r.StepId, comparer))
        foreach (var resource in ReadResources(request.ResourceIds).Distinct(comparer).Where(id => Saturated(task, id)))
        foreach (var lease in leases.Where(l => comparer.Equals(l.ResourceId, resource) &&
            !comparer.Equals(l.TaskId, task) && remaining.Contains(l.TaskId)))
            links.Add(new(task, request.StepId, request.VehicleId, lease.TaskId, lease.OwnerStepId,
                lease.VehicleId, lease.ResourceId, lease.ExpiresAt < now, lease.MapId, lease.MapVersionId));
        links = links.Distinct().OrderBy(l => l.WaitingTaskId, comparer).ThenBy(l => l.HoldingTaskId, comparer)
            .ThenBy(l => l.ResourceId, comparer).ThenBy(l => l.WaitingStepId, comparer)
            .ThenBy(l => l.HoldingStepId, comparer).ToList();
        var graph = remaining.ToDictionary(task => task, task => links.Where(l => comparer.Equals(l.WaitingTaskId, task))
            .Select(l => l.HoldingTaskId).Distinct(comparer).Order(comparer).ToArray(), comparer);

        // Iterative Kosaraju traversal avoids a recursion limit for large configured fleets.
        var visited = new HashSet<string>(comparer);
        var order = new List<string>();
        foreach (var task in remaining.Order(comparer))
        {
            if (!visited.Add(task)) continue;
            var stack = new Stack<(string Task, int Index)>(); stack.Push((task, 0));
            while (stack.TryPop(out var cursor))
            {
                if (cursor.Index == graph[cursor.Task].Length) { order.Add(cursor.Task); continue; }
                stack.Push((cursor.Task, cursor.Index + 1));
                var next = graph[cursor.Task][cursor.Index];
                if (visited.Add(next)) stack.Push((next, 0));
            }
        }
        var reverse = remaining.ToDictionary(task => task, _ => new List<string>(), comparer);
        foreach (var (task, destinations) in graph) foreach (var destination in destinations) reverse[destination].Add(task);
        visited.Clear(); var result = new List<TrafficCycleRisk>();
        foreach (var task in order.AsEnumerable().Reverse())
        {
            if (!visited.Add(task)) continue;
            var component = new HashSet<string>(comparer); var stack = new Stack<string>(); stack.Push(task);
            while (stack.TryPop(out var current))
            {
                component.Add(current);
                foreach (var next in reverse[current]) if (visited.Add(next)) stack.Push(next);
            }
            if (component.Count < 2) continue;
            var cycleLinks = links.Where(l => component.Contains(l.WaitingTaskId) && component.Contains(l.HoldingTaskId)).ToArray();
            var taskIds = component.Order(comparer).ToArray();
            var vehicleIds = cycleLinks.SelectMany(l => new[] { l.WaitingVehicleId, l.HoldingVehicleId }).Distinct(comparer).Order(comparer).ToArray();
            var resourceIds = cycleLinks.Select(l => l.ResourceId).Distinct(comparer).Order(comparer).ToArray();
            var key = JsonSerializer.Serialize(cycleLinks.Select(l => new[] { l.WaitingTaskId.ToUpperInvariant(),
                l.WaitingStepId.ToUpperInvariant(), l.WaitingVehicleId.ToUpperInvariant(),
                l.HoldingTaskId.ToUpperInvariant(), l.HoldingStepId.ToUpperInvariant(), l.HoldingVehicleId.ToUpperInvariant(),
                l.ResourceId.ToUpperInvariant(), l.MapId, l.MapVersionId }));
            var id = "WAIT-CYCLE-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            result.Add(new(id, taskIds, vehicleIds, resourceIds, cycleLinks,
                $"观察到{taskIds.Length}个任务的闭合资源等待风险：车辆{string.Join('、', vehicleIds)}；资源{string.Join('、', resourceIds)}。核对原命令、停车位置和交接证据后处置；不自动解锁、倒车或重发。该观察不证明物理死锁。"));
        }
        return result.OrderBy(r => r.CycleId, StringComparer.Ordinal).ToArray();
    }

    private static string[] ReadResources(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
}
