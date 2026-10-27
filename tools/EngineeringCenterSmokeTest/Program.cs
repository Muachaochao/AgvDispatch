using System.Diagnostics;
using System.Text.Json;
using AgvDispatch.WebHost.Engineering;

var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "agv-engineering-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
var checks = 0;
void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
async Task Reject(Func<Task> fn, string why) { try { await fn(); } catch (Exception e) when(e is ArgumentException || e.GetType().Name == "ProjectConflictException") { checks++; return; } throw new Exception(why); }
EngineeringVersion Version(EngineeringPlan p) => new(Guid.NewGuid().ToString("N"), "test", DateTimeOffset.UtcNow, "隔离测试", p, EngineeringStore.Hash(p));
EngineeringSimulationRequest Calls(EngineeringVersion v, int count = 0) => new(v.Id, v.Plan.Stations.Take(count == 0 ? v.Plan.Stations.Count : count).Select(s => new EngineeringCall("C-" + s.Id, s.Id)).ToList(), [], []);
void Verify(EngineeringPlan p, EngineeringSimulationReport r)
{
    Assert(r.Tasks.All(t => t.Steps.Length == 9 && t.End > t.Start), "每个模拟任务含九阶段及有效时间");
    foreach (var vehicle in r.Tasks.GroupBy(t => t.VehicleId))
    {
        double end = 0;
        foreach (var task in vehicle.OrderBy(t => t.Start)) { Assert(task.Start + 1e-7 >= end, "同车不能并发执行任务"); end = task.End; }
    }
    foreach (var resource in r.Reservations.GroupBy(x => x.ResourceId))
    {
        var capacity = resource.Key.StartsWith("@pickup:") ? 1 : p.Resources.Single(x => x.Id == resource.Key).Capacity;
        var active = 0;
        foreach (var point in resource.SelectMany(x => new[] { (At: x.Start, Delta: 1), (At: x.End, Delta: -1) }).OrderBy(x => x.At).ThenBy(x => x.Delta))
        { active += point.Delta; Assert(active >= 0 && active <= capacity, "资源容量不能超限：" + resource.Key); }
    }
    foreach (var task in r.Tasks)
    {
        Assert(p.Standbys.Any(b => b.NodeId == task.Route.Last()), "任务最终到达配置待机点");
        foreach (var pair in task.Route.Zip(task.Route.Skip(1))) Assert(p.Edges.Any(e => e.Enabled && (e.From == pair.First && e.To == pair.Second || e.Bidirectional && e.From == pair.Second && e.To == pair.First)), "路线不能跨越无道路节点");
    }
}
var timer = Stopwatch.StartNew();
var plan = EngineeringSample.Create(300, 5);
Assert(EngineeringValidator.Validate(plan).Valid, "300工位5车示例应通过校验");
var store = new EngineeringStore(Path.Combine(output, "store"));
var initial = await store.Draft(default);
var draft = await store.Save(new(initial.Revision, plan), default);
await Reject(() => store.Save(new(initial.Revision, plan), default), "过期修订不能覆盖草稿");
var reopened = new EngineeringStore(Path.Combine(output, "store"));
Assert((await reopened.Draft(default)).Revision == draft.Revision, "重建存储实例后草稿保留");
var published = await store.Publish(new(draft.Revision, "300工位5车基线"), default);
Assert(published.ContentHash == EngineeringStore.Hash(plan), "发布摘要应一致");
var incomplete = plan with { MapVersion = "" };
var bad = await store.Save(new(draft.Revision, incomplete), default);
Assert((await store.Draft(default)).Plan.MapVersion == "", "不完整草稿仍可保存");
await Reject(() => store.Publish(new(bad.Revision, "不应发布"), default), "不完整草稿不能发布");
Assert((await store.Version(published.Id, default)).Plan.MapVersion == "SIM-1", "后续草稿不能改变已发布版本");
var report = await store.Simulate(Calls(published), default);
Assert(report.Tasks.Count == 300 && report.Blocked.Count == 0, "300次呼叫应全部建模完成");
Assert(report.PeakVehicles == 5, "基线应展示5车模型并行");
Assert(report.FinalPositions.Values.Distinct().Count() == 5, "5车应有独立待机点");
Verify(plan, report);
Assert((await reopened.Report(report.Id, default)).Tasks.Count == 300, "模拟报告可重新读取");
var duplicate = Calls(published, 3); duplicate.Calls.Add(duplicate.Calls[0]);
var dedup = EngineeringSimulator.Run(published, duplicate);
Assert(dedup.Tasks.Count == 3 && dedup.DuplicateCalls == 1, "同一呼叫去重");
duplicate.Calls.Add(duplicate.Calls[0] with { StationId = "CNC-002" });
await Reject(() => Task.FromResult(EngineeringSimulator.Run(published, duplicate)), "相同呼叫编号不能变更语义");
var offline = EngineeringSimulator.Run(published, Calls(published, 8) with { OfflineVehicles = ["AGV-01"] });
Assert(offline.Tasks.Count == 8 && offline.Tasks.All(t => t.VehicleId != "AGV-01"), "离线车辆不能派发"); Verify(plan, offline);
var blocked = EngineeringSimulator.Run(published, Calls(published, 3) with { BlockedResources = ["DUMP-AREA"] });
Assert(blocked.Tasks.Count == 0 && blocked.Blocked.Count == 3 && blocked.Blocked.All(b => b.Link.Contains("dumpers")), "封锁倒料区应列明阻塞与配置入口");
var small = EngineeringSample.Create(4, 2);
var low = small with { Vehicles = small.Vehicles.Select(v => v with { Battery = 5 }).ToList() };
var lowV = Version(low); var lowReport = EngineeringSimulator.Run(lowV, Calls(lowV));
Assert(lowReport.Tasks.Count == 0 && lowReport.Blocked.All(b => b.Reason.Contains("电量")), "低电量拒绝原因明确");
var allOffline = EngineeringSimulator.Run(published, Calls(published, 1) with { OfflineVehicles = plan.Vehicles.Select(v => v.Id).ToArray() });
Assert(allOffline.Blocked.Single().Reason.Contains("离线"), "全车离线有明确原因");
var missing = small with { Signals = small.Signals.Where(s => s.Name != "EmptyBoxPlaced").ToList() };
Assert(EngineeringValidator.Validate(missing).Issues.Any(x => x.Message.Contains("EmptyBoxPlaced")), "缺少放箱反馈应指明信号");
var conflict = small with { Signals = [..small.Signals, small.Signals[0] with { Id = "CONFLICT", Address = "DB1.DBX04000.0" }] };
Assert(EngineeringValidator.Validate(conflict).Issues.Any(x => x.Message.Contains("冲突")), "前导零地址也应查出冲突");
var wrongDirection = small with { Signals = small.Signals.Select(s => s.Name == "EmptyBoxPlaced" ? s with { Direction = "Read" } : s).ToList() };
Assert(!EngineeringValidator.Validate(wrongDirection).Valid, "错误读写方向拒绝发布");
var disconnected = small with { Edges = small.Edges.Where(e => e.Id != "WAIT-DUMP").ToList() };
Assert(EngineeringValidator.Validate(disconnected).Issues.Any(x => x.Message.Contains("路线不完整")), "不可达路线拒绝发布");
var noCapacity = small with { Standbys = [] };
Assert(EngineeringValidator.Validate(noCapacity).Issues.Any(x => x.Message.Contains("容量")), "待机容量不足应阻塞");
var sameHome = small with { Standbys = small.Standbys.Select(b => b with { NodeId = "WAIT" }).ToList() };
Assert(EngineeringValidator.Validate(sameHome).Issues.Any(x => x.Message.Contains("倒料等待位")), "倒料等待位不能当普通待机点");
var malformed = small with { Signals = null! };
await Reject(() => Task.FromResult(EngineeringValidator.Validate(malformed)), "null数组应拒绝");
var traffic = small with { Edges = small.Edges.Select(e => e.Id == "MAIN-WAIT" ? e with { ResourceIds = ["AISLE"] } : e).ToList(), Resources = [..small.Resources, new("AISLE", "共享窄道", "NarrowAisle")] };
var trafficV = Version(traffic); var trafficReport = EngineeringSimulator.Run(trafficV, Calls(trafficV)); Verify(traffic, trafficReport);
Assert(trafficReport.Reservations.Any(r => r.ResourceId == "AISLE"), "绑定窄道参与容量预约");
var unavailable = EngineeringSimulator.Run(trafficV, Calls(trafficV) with { BlockedResources = ["AISLE"] });
Assert(unavailable.Tasks.Count == 0 && unavailable.Blocked.All(b => b.Reason.Contains("不可达")), "封锁道路应保留无法分配原因");
// A second dumper and disjoint service pools exercise configuration rather than CNC-001/002 assumptions.
var multi = EngineeringSample.Create(6, 2);
multi.Nodes.AddRange([new("WAIT-B", "SIM-WAIT-B", 0, 15), new("DUMP-B", "SIM-DUMP-B", 0, 17)]);
multi.Resources.AddRange([new("WAIT-BAY-B", "B等待", "Waiting"), new("DUMP-AREA-B", "B倒料", "Dumper")]);
multi.Edges.AddRange([new("E-WAIT-B", "MAIN", "WAIT-B", 15, .5, []), new("E-DUMP-B", "WAIT-B", "DUMP-B", 2, .3, [])]);
multi.Dumpers.Add(new("DUMPER-002", "B倒料", "PLC-SIM", "WAIT-B", "DUMP-B", "WAIT-BAY-B", "DUMP-AREA-B"));
multi.Signals.AddRange(multi.Signals.Where(s => s.OwnerId == "DUMPER-001").Select((s, i) => s with { Id = s.Id.Replace("001", "002"), OwnerId = "DUMPER-002", Address = $"DB1.DBX4001.{i}" }).ToArray());
multi = multi with { Pools = [new("POOL-A", "A", ["AGV-01"]), new("POOL-B", "B", ["AGV-02"])], Standbys = multi.Standbys.Select((b,i) => b with { PoolId = i == 0 ? "POOL-A" : "POOL-B" }).ToList(), Stations = multi.Stations.Select((s,i) => i % 2 == 0 ? s : s with { PoolId = "POOL-B", DumperId = "DUMPER-002" }).ToList() };
Assert(EngineeringValidator.Validate(multi).Valid, "多倒料设备独立车辆池应通过");
var multiV = Version(multi); var multiR = EngineeringSimulator.Run(multiV, Calls(multiV)); Verify(multi, multiR);
Assert(multiR.Tasks.All(t => t.VehicleId == (int.Parse(t.StationId[4..]) % 2 == 1 ? "AGV-01" : "AGV-02")), "车辆不跨未授权车辆池");
Assert(multiR.Reservations.Any(r => r.ResourceId == "DUMP-AREA-B"), "B倒料设备参与预约");
timer.Stop();
await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { checks, elapsedSeconds = timer.Elapsed.TotalSeconds, stations = 300, vehicles = 5, tasks = report.Tasks.Count, report.PeakVehicles, report.DurationSeconds, baselineReport = report.Id, evidenceBoundary = report.EvidenceBoundary }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {checks} assertions; 300 stations / 5 vehicles / {report.Tasks.Count} tasks; peak {report.PeakVehicles}; {timer.Elapsed.TotalSeconds:F2}s. Artifacts: {output}");
