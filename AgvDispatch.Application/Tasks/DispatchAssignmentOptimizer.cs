using System.Diagnostics;

namespace AgvDispatch.Application.Abstractions;

/// <summary>
/// Pure recommendation engine: no repositories, leases, or vehicle commands. Maximizes feasible
/// assignment count, then effective priority, FIFO age, estimated arrival time and secondary costs.
/// Vector costs avoid mixing priority points with seconds or overflowing a giant scalar penalty.
/// </summary>
public sealed class DispatchAssignmentOptimizer
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    public DispatchAssignmentTaskScore Score(DispatchAssignmentTask task, DispatchAssignmentOptions options, DateTimeOffset now)
    {
        var waitingSeconds = Math.Max(0, (now - task.CalledAt).TotalSeconds);
        var wait = Math.Min(options.MaxWaitPriority, waitingSeconds / 60 * options.WaitPriorityPerMinute);
        var deadline = task.Deadline is { } due
            ? options.MaxDeadlinePriority * Math.Clamp(1 - (due - now).TotalMinutes / options.DeadlineHorizonMinutes, 0, 1)
            : 0;
        return new(task.TaskId, task.StationId, task.BasePriority, task.CalledAt, waitingSeconds,
            wait, deadline, task.BasePriority + wait + deadline);
    }

    public IReadOnlyList<DispatchAssignmentTaskScore> SelectWindow(IReadOnlyList<DispatchAssignmentTask> tasks,
        DispatchAssignmentOptions options, DateTimeOffset now)
    {
        Validate(options);
        return OrderedScores(tasks, options, now).Take(options.WindowSize).ToArray();
    }

    public DispatchAssignmentResult Optimize(IReadOnlyList<DispatchAssignmentTask> tasks,
        IReadOnlyList<DispatchAssignmentCandidate> candidates, DispatchAssignmentOptions options,
        DateTimeOffset now, int maxAssignments = int.MaxValue)
    {
        Validate(options);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAssignments);
        var watch = Stopwatch.StartNew();
        var allScores = OrderedScores(tasks, options, now);
        var window = allScores.Take(options.WindowSize).ToArray();
        var byTask = window.ToDictionary(t => t.TaskId, IdComparer);
        var edges = candidates.Where(c => byTask.ContainsKey(c.TaskId) && Valid(c, options))
            .GroupBy(c => (c.TaskId.ToUpperInvariant(), c.VehicleId.ToUpperInvariant()))
            .Select(g => g.OrderBy(c => Arrival(c)).ThenBy(c => Secondary(c))
                .ThenBy(c => c.VehicleId, StringComparer.Ordinal).First()).ToArray();
        var retained = Sparsify(edges, options);
        var selected = Match(window, retained, maxAssignments, priorityOnly: false);

        // Top-K pruning alone can destroy an alternating path even when every task keeps two
        // vehicles. Repair with a full feasibility/priority matching, without extra route queries.
        if (retained.Count != edges.Length)
        {
            var protectedPairs = Match(window, edges, maxAssignments, priorityOnly: true);
            if (selected.Count != protectedPairs.Count ||
                PrioritySum(selected, byTask) < PrioritySum(protectedPairs, byTask) ||
                (PrioritySum(selected, byTask) == PrioritySum(protectedPairs, byTask) &&
                 AgeSum(selected, byTask) > AgeSum(protectedPairs, byTask)))
            {
                var ids = retained.Select(PairId).ToHashSet();
                foreach (var pair in protectedPairs)
                    if (ids.Add(PairId(pair))) retained.Add(pair);
                selected = Match(window, retained, maxAssignments, priorityOnly: false);
            }
        }

        var recommendations = selected.Select(c => Recommend(byTask[c.TaskId], c))
            .OrderByDescending(a => a.EffectivePriority)
            .ThenBy(a => byTask[a.TaskId].CalledAt)
            .ThenBy(a => a.TaskId, IdComparer).ToArray();
        var assigned = recommendations.Select(r => r.TaskId).ToHashSet(IdComparer);
        var withCandidates = edges.Select(c => c.TaskId).ToHashSet(IdComparer);
        var unassigned = allScores.Where(t => !assigned.Contains(t.TaskId)).Select(t =>
            new DispatchAssignmentUnassigned(t.TaskId, t.StationId, t.EffectivePriority,
                !byTask.ContainsKey(t.TaskId) ? "等待进入滚动窗口；原任务保留在持久化队列。" :
                !withCandidates.Contains(t.TaskId) ? "没有满足全部硬约束的任务—车辆候选。" :
                "本轮车辆或并发名额不足，等待下一轮；不抢占已派任务。" )).ToArray();
        watch.Stop();
        return new()
        {
            TaskScores = window, Assignments = recommendations, Unassigned = unassigned,
            InputCandidateCount = edges.Length, RetainedCandidateCount = retained.Count,
            TotalTravelSeconds = recommendations.Sum(r => r.TravelSeconds),
            TotalCostSeconds = recommendations.Sum(r => r.TotalCostSeconds),
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
        };
    }

    private DispatchAssignmentTaskScore[] OrderedScores(IReadOnlyList<DispatchAssignmentTask> tasks,
        DispatchAssignmentOptions options, DateTimeOffset now) => tasks
        .Where(t => !string.IsNullOrWhiteSpace(t.TaskId))
        .GroupBy(t => t.TaskId, IdComparer).Select(g => g.OrderBy(t => t.CalledAt).First())
        .Select(t => Score(t, options, now)).OrderByDescending(t => t.EffectivePriority)
        .ThenBy(t => t.CalledAt).ThenBy(t => t.TaskId, IdComparer).ThenBy(t => t.TaskId, StringComparer.Ordinal).ToArray();

    private static void Validate(DispatchAssignmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.GetConfigurationError() is { } error) throw new ArgumentException(error, nameof(options));
    }

    private static bool Valid(DispatchAssignmentCandidate c, DispatchAssignmentOptions options) =>
        !string.IsNullOrWhiteSpace(c.VehicleId) &&
        new[] { c.TravelSeconds, c.TrafficWaitSeconds, c.ReturnSeconds, c.BatteryRiskSeconds, c.CrossZoneSeconds }
            .All(v => double.IsFinite(v) && v is >= 0 and <= 10_000_000) &&
        double.IsFinite(c.StandbyBonusSeconds) && c.StandbyBonusSeconds >= 0 &&
        c.StandbyBonusSeconds <= options.StandbyBonusSeconds;

    private static List<DispatchAssignmentCandidate> Sparsify(DispatchAssignmentCandidate[] edges, DispatchAssignmentOptions options)
    {
        var selected = edges.GroupBy(c => c.VehicleId, IdComparer)
            .SelectMany(g => g.OrderBy(Arrival).ThenBy(Secondary).ThenBy(c => c.TaskId, IdComparer).Take(options.CandidatesPerVehicle))
            .Concat(edges.GroupBy(c => c.TaskId, IdComparer)
                .SelectMany(g => g.OrderBy(Arrival).ThenBy(Secondary).ThenBy(c => c.VehicleId, IdComparer).Take(options.MinimumCandidatesPerTask)));
        return selected.DistinctBy(PairId).ToList();
    }

    private static (string, string) PairId(DispatchAssignmentCandidate candidate) =>
        (candidate.TaskId.ToUpperInvariant(), candidate.VehicleId.ToUpperInvariant());
    private static double Arrival(DispatchAssignmentCandidate c) => c.TravelSeconds + c.TrafficWaitSeconds - c.StandbyBonusSeconds;
    private static double Secondary(DispatchAssignmentCandidate c) => c.ReturnSeconds + c.BatteryRiskSeconds + c.CrossZoneSeconds;
    private static decimal PrioritySum(IEnumerable<DispatchAssignmentCandidate> selected, Dictionary<string, DispatchAssignmentTaskScore> scores) =>
        selected.Sum(c => (decimal)scores[c.TaskId].EffectivePriority);
    private static long AgeSum(IEnumerable<DispatchAssignmentCandidate> selected, Dictionary<string, DispatchAssignmentTaskScore> scores) =>
        selected.Sum(c => scores[c.TaskId].CalledAt.ToUnixTimeMilliseconds());

    private static DispatchAssignmentRecommendation Recommend(DispatchAssignmentTaskScore task, DispatchAssignmentCandidate c) =>
        new(task.TaskId, task.StationId, c.VehicleId, task.EffectivePriority, task.WaitingSeconds,
            c.TravelSeconds, c.TrafficWaitSeconds, c.ReturnSeconds, c.BatteryRiskSeconds, c.CrossZoneSeconds,
            c.StandbyBonusSeconds, c.TotalCostSeconds,
            $"全局匹配推荐；有效优先级 {task.EffectivePriority:0.###}（基础 {task.BasePriority} + 等待 {task.WaitPriority:0.###} + 截止 {task.DeadlinePriority:0.###}），预计行驶 {c.TravelSeconds:0.###} 秒；仍须逐项取得有效路权并复核后才能发车。");

    private static List<DispatchAssignmentCandidate> Match(DispatchAssignmentTaskScore[] tasks,
        IReadOnlyList<DispatchAssignmentCandidate> candidates, int maximumCount, bool priorityOnly)
    {
        if (tasks.Length == 0 || candidates.Count == 0 || maximumCount == 0) return [];
        var vehicles = candidates.Select(c => c.VehicleId).Distinct(IdComparer).OrderBy(v => v, IdComparer).ThenBy(v => v, StringComparer.Ordinal).ToArray();
        var taskIndex = tasks.Select((t, i) => (t.TaskId, Index: i)).ToDictionary(t => t.TaskId, t => t.Index, IdComparer);
        var vehicleIndex = vehicles.Select((v, i) => (Id: v, Index: i)).ToDictionary(v => v.Id, v => v.Index, IdComparer);
        var source = 0;
        var vehicleStart = 1 + tasks.Length;
        var sink = vehicleStart + vehicles.Length;
        var graph = Enumerable.Range(0, sink + 1).Select(_ => new List<FlowEdge>()).ToArray();
        var maximumPriority = tasks.Max(t => (decimal)t.EffectivePriority);
        void Add(int from, int to, Cost cost, DispatchAssignmentCandidate? pair = null)
        {
            var forward = new FlowEdge(to, graph[to].Count, 1, cost, pair);
            var reverse = new FlowEdge(from, graph[from].Count, 0, -cost, null);
            graph[from].Add(forward); graph[to].Add(reverse);
        }
        for (var t = 0; t < tasks.Length; t++)
            Add(source, 1 + t, new(maximumPriority - (decimal)tasks[t].EffectivePriority, tasks[t].CalledAt.ToUnixTimeMilliseconds(), 0, 0, 0));
        foreach (var c in candidates.OrderBy(c => taskIndex[c.TaskId]).ThenBy(c => vehicleIndex[c.VehicleId]))
        {
            var ti = taskIndex[c.TaskId]; var vi = vehicleIndex[c.VehicleId];
            Add(1 + ti, vehicleStart + vi, priorityOnly ? default :
                new(0, 0, Milliseconds(Arrival(c)), Milliseconds(Secondary(c)), (long)vi * (tasks.Length - ti)), c);
        }
        for (var v = 0; v < vehicles.Length; v++) Add(vehicleStart + v, sink, default);

        // Successive shortest augmenting paths with Bellman-Ford. Negative reverse edges
        // reassign earlier choices, solving the scarce-vehicle failure of per-task greediness.
        for (var flow = 0; flow < Math.Min(maximumCount, vehicles.Length); flow++)
        {
            var distance = new Cost?[graph.Length];
            var fromNode = Enumerable.Repeat(-1, graph.Length).ToArray();
            var fromEdge = new int[graph.Length];
            distance[source] = default(Cost);
            for (var pass = 0; pass < graph.Length - 1; pass++)
            {
                var changed = false;
                for (var n = 0; n < graph.Length; n++)
                {
                    if (distance[n] is not { } current) continue;
                    for (var e = 0; e < graph[n].Count; e++)
                    {
                        var edge = graph[n][e];
                        if (edge.Capacity == 0) continue;
                        var alternative = current + edge.Cost;
                        if (distance[edge.To] is { } existing && alternative.CompareTo(existing) >= 0) continue;
                        distance[edge.To] = alternative; fromNode[edge.To] = n; fromEdge[edge.To] = e; changed = true;
                    }
                }
                if (!changed) break;
            }
            if (distance[sink] is null) break;
            for (var n = sink; n != source; n = fromNode[n])
            {
                var edge = graph[fromNode[n]][fromEdge[n]];
                edge.Capacity--; graph[n][edge.Reverse].Capacity++;
            }
        }
        return graph.SelectMany(g => g).Where(e => e.Pair is not null && e.Capacity == 0).Select(e => e.Pair!).ToList();
    }

    private static long Milliseconds(double seconds) => checked((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));

    private sealed class FlowEdge(int to, int reverse, int capacity, Cost cost, DispatchAssignmentCandidate? pair)
    {
        public int To { get; } = to;
        public int Reverse { get; } = reverse;
        public int Capacity { get; set; } = capacity;
        public Cost Cost { get; } = cost;
        public DispatchAssignmentCandidate? Pair { get; } = pair;
    }

    private readonly record struct Cost(decimal Priority, long Age, long Arrival, long Secondary, long Tie) : IComparable<Cost>
    {
        public int CompareTo(Cost other)
        {
            var priority = Priority.CompareTo(other.Priority); if (priority != 0) return priority;
            var age = Age.CompareTo(other.Age); if (age != 0) return age;
            var arrival = Arrival.CompareTo(other.Arrival); if (arrival != 0) return arrival;
            var secondary = Secondary.CompareTo(other.Secondary); if (secondary != 0) return secondary;
            return Tie.CompareTo(other.Tie);
        }
        public static Cost operator +(Cost x, Cost y) => new(x.Priority + y.Priority, x.Age + y.Age, x.Arrival + y.Arrival, x.Secondary + y.Secondary, x.Tie + y.Tie);
        public static Cost operator -(Cost x) => new(-x.Priority, -x.Age, -x.Arrival, -x.Secondary, -x.Tie);
    }
}
