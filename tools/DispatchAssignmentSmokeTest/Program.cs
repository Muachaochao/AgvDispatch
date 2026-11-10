using System.Diagnostics;
using AgvDispatch.Application.Abstractions;

var optimizer = new DispatchAssignmentOptimizer();
var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
var options = new DispatchAssignmentOptions();
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
DispatchAssignmentTask Task(string id, int priority = 50, double waitMinutes = 0) => new(id, "STATION-" + id, priority, now.AddMinutes(-waitMinutes));
DispatchAssignmentCandidate Pair(string task, string vehicle, double travel) => new(task, vehicle, travel);
DispatchAssignmentResult Solve(DispatchAssignmentTask[] tasks, DispatchAssignmentCandidate[] pairs, DispatchAssignmentOptions? settings = null, int slots = int.MaxValue) =>
    optimizer.Optimize(tasks, pairs, settings ?? options, now, slots);

Check(options.Mode == "Shadow" && options.GetConfigurationError() is null, "defaults recommend in shadow mode");
var detours = new AgvDispatch.Application.Tasks.TemporaryDetourDeferral();
Check(detours.RemainingSeconds("T", "V", "M", 270, 10, now) == 120,
    "temporary 27x detour first waits without granting a route");
Check(detours.RemainingSeconds("T", "V", "M", 270, 10, now.AddSeconds(119)) == 1 &&
    detours.RemainingSeconds("T", "V", "M", 270, 10, now.AddSeconds(120)) == 0,
    "temporary detour wait is bounded and does not restart on every dispatch cycle");
Check(detours.RemainingSeconds("T", "V", "M", 11, 10, now.AddSeconds(121)) == 0 &&
    detours.RemainingSeconds("T", "V", "M", 270, 10, now.AddSeconds(122)) == 120,
    "restored short route clears the old congestion episode");
Check(detours.RemainingSeconds("T", "V2", "M", 270, 10, now.AddSeconds(180)) == 120,
    "different candidate vehicles keep independent congestion windows");
Check(detours.RemainingSeconds("SMALL", "V", "M", 30, 2, now) == 0 &&
    detours.RemainingSeconds("RATIO", "V", "M", 100, 40, now) == 0,
    "small absolute detours and ordinary alternative routes remain immediately eligible");
detours.RetainTasks(new HashSet<string>());
Check(detours.RemainingSeconds("T", "V", "M", 270, 10, now.AddSeconds(300)) == 120,
    "finished or removed tasks leave no stale detour timer");
Check((options with { IntervalMs = 1999 }).GetConfigurationError() is not null &&
      (options with { IntervalMs = 5001 }).GetConfigurationError() is not null, "rolling interval constrained to 2–5 seconds");
Check((options with { StandbyBonusSeconds = 2 }).GetConfigurationError() is not null &&
      (options with { WaitPriorityPerMinute = double.NaN }).GetConfigurationError() is not null, "reject unbounded standby and nonfinite settings");

var score = optimizer.Score(Task("AGING", 50, 10), options, now);
Check(score.WaitPriority == 10 && score.EffectivePriority == 60, "waiting priority counted once");
Check(optimizer.Score(Task("CAP", 50, 90), options, now).WaitPriority == 30, "waiting bonus capped");
Check(optimizer.Score(Task("FUTURE", 50, -1), options, now).WaitPriority == 0, "future call timestamp cannot create negative waiting");
Check(optimizer.Score(Task("DUE") with { Deadline = now }, options, now).DeadlinePriority == 30 &&
      optimizer.Score(Task("NOT-DUE") with { Deadline = now.AddHours(1) }, options, now).DeadlinePriority == 0,
    "deadline urgency uses nullable explicit deadline");

var scarce = Solve([Task("A"), Task("B")], [Pair("A", "AGV-01", 8), Pair("A", "AGV-03", 5), Pair("B", "AGV-03", 5)]);
Check(scarce.Assignments.Count == 2 && scarce.Assignments.Single(a => a.TaskId == "A").VehicleId == "AGV-01" &&
      scarce.Assignments.Single(a => a.TaskId == "B").VehicleId == "AGV-03", "residual paths reserve unique vehicle for constrained station");
Check(scarce.Assignments.Select(a => a.VehicleId).Distinct().Count() == 2, "each vehicle receives at most one task");

var globalCost = Solve([Task("A"), Task("B")], [Pair("A", "1", 1), Pair("A", "2", 3), Pair("B", "1", 2), Pair("B", "2", 100)]);
Check(globalCost.TotalTravelSeconds == 5, "joint arrival time optimized rather than greedy per-task route");
var priority = Solve([Task("LOW", 10), Task("HIGH", 90)], [Pair("LOW", "V", 1), Pair("HIGH", "V", 100000)]);
Check(priority.Assignments.Single().TaskId == "HIGH", "priority cannot be outweighed by route seconds");
var costly = Solve([Task("A")], [Pair("A", "V", 100000)]);
Check(costly.Assignments.Count == 1, "positive route cost never suppresses a feasible assignment");
var aging = Solve([Task("OLD", 50, 10), Task("NEW", 55)], [Pair("OLD", "V", 100), Pair("NEW", "V", 1)]);
Check(aging.Assignments.Single().TaskId == "OLD", "waiting compensation changes priority order");
var fifo = Solve([Task("OLD", 50, 100), Task("NEW", 50, 60)], [Pair("OLD", "V", 100), Pair("NEW", "V", 1)]);
Check(fifo.Assignments.Single().TaskId == "OLD", "FIFO preserved after waiting cap for equal effective priority");

var standbyLong = Solve([Task("A")], [Pair("A", "STANDBY", 8) with { StandbyBonusSeconds = 1 }, Pair("A", "OTHER", 5)]);
Check(standbyLong.Assignments.Single().VehicleId == "OTHER", "standby bonus cannot cover a clearly longer empty trip");
var standbyClose = Solve([Task("A")], [Pair("A", "STANDBY", 5.5) with { StandbyBonusSeconds = 1 }, Pair("A", "OTHER", 5)]);
Check(standbyClose.Assignments.Single().VehicleId == "STANDBY", "standby may win within one second");
var secondary = Solve([Task("A")], [Pair("A", "1", 5) with { ReturnSeconds = 20 }, Pair("A", "2", 5) with { ReturnSeconds = 5 }]);
Check(secondary.Assignments.Single().VehicleId == "2", "equal arrival uses return cost");
var traffic = Solve([Task("A")], [Pair("A", "1", 5) with { TrafficWaitSeconds = 20 }, Pair("A", "2", 6)]);
Check(traffic.Assignments.Single().VehicleId == "2", "traffic waiting contributes to estimated arrival");
var invalid = Solve([Task("A")], [Pair("A", "BAD", double.NaN), Pair("A", "BAD2", -1), Pair("A", "BAD3", 1) with { StandbyBonusSeconds = 10 }, Pair("A", "GOOD", 5)]);
Check(invalid.Assignments.Single().VehicleId == "GOOD" && invalid.InputCandidateCount == 1, "invalid costs never enter matching graph");
var slotLimit = Solve([Task("A", 10), Task("B", 90)], [Pair("A", "1", 1), Pair("B", "2", 1)], slots: 1);
Check(slotLimit.Assignments.Single().TaskId == "B", "concurrency slots constrain recommendation count");
Check(Solve([Task("A")], [Pair("A", "1", 1)], slots: 0).Assignments.Count == 0, "zero slots never recommends execution");
var window = Solve([Task("OLD", 50, 20), Task("NEW", 60)], [Pair("OLD", "1", 1), Pair("NEW", "2", 1)], options with { WindowSize = 1 });
Check(window.TaskScores.Single().TaskId == "OLD" && window.Unassigned.Single().TaskId == "NEW" &&
      window.Unassigned.Single().Reason.Contains("滚动窗口"), "window orders dynamically and preserves deferred tasks");
var unmatched = Solve([Task("A"), Task("B")], [Pair("A", "1", 1)]);
Check(unmatched.Unassigned.Single().TaskId == "B" && unmatched.Unassigned.Single().Reason.Contains("硬约束"), "unmatched tasks retain clear exclusion reason");

var repairPairs = new[] { Pair("A", "1", 0), Pair("A", "2", 1), Pair("A", "3", 2),
    Pair("B", "1", 0), Pair("B", "2", 1), Pair("C", "1", 0), Pair("C", "2", 1) };
var repair = Solve([Task("A"), Task("B"), Task("C")], repairPairs,
    options with { CandidatesPerVehicle = 1, MinimumCandidatesPerTask = 1 });
Check(repair.Assignments.Count == 3, "sparse top-K pruning repairs missing alternating paths to protect cardinality");
var sole = Solve([Task("A"), Task("B")], [Pair("A", "1", 0), Pair("B", "1", 100)],
    options with { CandidatesPerVehicle = 1, MinimumCandidatesPerTask = 1 });
Check(sole.RetainedCandidateCount == 2, "sole-vehicle candidate survives per-vehicle top-K pruning");

// Independent enumeration only in tests: compare every small random graph against the
// mathematical max-cardinality, max-priority, min-arrival, min-secondary objective.
var random = new Random(20261103);
for (var sample = 0; sample < 250; sample++)
{
    var taskCount = random.Next(1, 7); var vehicleCount = random.Next(1, 6);
    var tasks = Enumerable.Range(0, taskCount).Select(i => Task("T" + i, random.Next(1, 6))).ToArray();
    var pairs = tasks.SelectMany(t => Enumerable.Range(0, vehicleCount).Where(_ => random.NextDouble() < .65)
        .Select(v => Pair(t.TaskId, "V" + v, random.Next(1, 30)) with { ReturnSeconds = random.Next(0, 10) })).ToArray();
    var slots = random.Next(1, vehicleCount + 1);
    var actual = Solve(tasks, pairs, options with { CandidatesPerVehicle = 100, MinimumCandidatesPerTask = 5 }, slots);
    var expected = BruteForce(tasks, pairs, slots);
    var actualObjective = (actual.Assignments.Count, actual.Assignments.Sum(a => a.EffectivePriority),
        actual.Assignments.Sum(a => a.TravelSeconds), actual.Assignments.Sum(a => a.ReturnSeconds));
    if (actualObjective != expected) throw new InvalidOperationException($"Random graph {sample}: {actualObjective} != {expected}");
    var reversed = Solve(tasks.Reverse().ToArray(), pairs.Reverse().ToArray(), options, slots);
    if (string.Join(";", actual.Assignments.Select(a => a.TaskId + ":" + a.VehicleId)) !=
        string.Join(";", reversed.Assignments.Select(a => a.TaskId + ":" + a.VehicleId)))
        throw new InvalidOperationException("Input order changed deterministic result " + sample);
}
Check(true, "250 random graphs match independent exhaustive optimality checks and deterministic reversed input");

var scaleTasks = Enumerable.Range(0, 300).Select(i => Task("S" + i.ToString("D3"), random.Next(1, 100), random.Next(0, 60))).ToArray();
var scalePairs = scaleTasks.SelectMany(t => Enumerable.Range(0, 12).Select(v => Pair(t.TaskId, "AGV-" + v.ToString("D2"), random.Next(1, 1000)))).ToArray();
var timer = Stopwatch.StartNew();
var scale = Solve(scaleTasks, scalePairs);
timer.Stop();
Check(scale.TaskScores.Count == 80 && scale.Assignments.Count == 12 && scale.InputCandidateCount == 960,
    "300 persisted tasks bounded to 80-task window with twelve unique vehicles");
Console.WriteLine($"Scale: {scale.InputCandidateCount} input edges, {scale.RetainedCandidateCount} retained; {timer.Elapsed.TotalMilliseconds:0.00} ms (not a field latency guarantee).");
Console.WriteLine($"Dispatch assignment: {checks} checks passed; pure calculations, no vehicle or PLC connections.");

static (int Count, double Priority, double Travel, double Return) BruteForce(DispatchAssignmentTask[] tasks, DispatchAssignmentCandidate[] pairs, int slots)
{
    (int Count, double Priority, double Travel, double Return) best = (0, 0, 0, 0);
    var used = new HashSet<string>();
    void Visit(int index, int count, double priority, double travel, double returning)
    {
        if (index == tasks.Length || count == slots)
        {
            var result = (Count: count, Priority: priority, Travel: travel, Return: returning);
            if (count > best.Count || count == best.Count && (priority > best.Priority || priority == best.Priority &&
                (travel < best.Travel || travel == best.Travel && returning < best.Return))) best = result;
            return;
        }
        Visit(index + 1, count, priority, travel, returning);
        foreach (var c in pairs.Where(c => c.TaskId == tasks[index].TaskId && !used.Contains(c.VehicleId)))
        {
            used.Add(c.VehicleId);
            Visit(index + 1, count + 1, priority + tasks[index].BasePriority, travel + c.TravelSeconds, returning + c.ReturnSeconds);
            used.Remove(c.VehicleId);
        }
    }
    Visit(0, 0, 0, 0, 0);
    return best;
}
