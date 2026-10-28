using AgvDispatch.WebHost.Engineering;

internal static class StandbyAllocationChecks
{
    public static void Run(Action<bool, string> check)
    {
        EngineeringStandbyAllocation Match(EngineeringPlan p, string[]? offline = null, string[]? blocked = null) =>
            EngineeringStandbyAllocator.Allocate(p, new(p), (offline ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase), (blocked ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
        EngineeringPlan FromIndependentStaging(EngineeringPlan p)
        {
            var staging = p.Vehicles.Select((v, i) => new EngineeringNode("STAGE-" + v.Id, "SIM-STAGE-" + v.Id, -10, -i * 2)).ToArray();
            return p with
            {
                Nodes = [..p.Nodes, ..staging],
                Edges = [..p.Edges, ..staging.Select(n => new EngineeringEdge("E-" + n.Id, n.Id, "MAIN", 10, .5, []))],
                Vehicles = p.Vehicles.Select((v, i) => v with { StartNode = staging[i].Id }).ToList()
            };
        }
        void Safe(EngineeringPlan p, EngineeringStandbyAllocation result)
        {
            check(result.Assignments.Values.Select(a => a.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Assignments.Count,
                "不同车辆不能分配同一物理待机点");
            foreach (var group in result.Assignments.Values.GroupBy(a => a.ResourceId, StringComparer.OrdinalIgnoreCase))
                check(group.Count() <= p.Resources.Single(r => r.Id.Equals(group.Key, StringComparison.OrdinalIgnoreCase)).Capacity,
                    "独立待机节点仍须受共享资源容量限制");
            foreach (var assignment in result.Assignments.Values)
            {
                var bay = p.Standbys.Single(b => b.Id == assignment.StandbyId);
                check(p.Pools.Single(pool => pool.Id == bay.PoolId).VehicleIds.Contains(assignment.VehicleId, StringComparer.OrdinalIgnoreCase),
                    "待机位匹配不能跨越未授权车辆池");
            }
        }

        // Each vehicle has two choices, but the original parked vehicles do not have permission
        // to exchange places implicitly. This old fixture must be rejected without repositioning.
        var constrained = EngineeringSample.Create(6, 3);
        var ids = constrained.Vehicles.Select(v => v.Id).ToArray();
        constrained = constrained with
        {
            Pools = [new("POOL-A", "公共", ids), new("ONLY-FIRST", "首车", [ids[0]]), new("LAST-TWO", "后两车", [ids[1], ids[2]])],
            Standbys = constrained.Standbys.Select((b, i) => b with { PoolId = i == 0 ? "POOL-A" : i == 1 ? "ONLY-FIRST" : "LAST-TWO", Priority = i == 0 ? 100 : 10 }).ToList()
        };
        var parkedValidation = EngineeringValidator.Validate(constrained);
        check(!parkedValidation.Valid && parkedValidation.Issues.Any(i => i.Message.Contains(ids[1]) && i.Message.Contains("原位占用")),
            "受限车辆的原待机点无授权且其他点有车时，明确拒绝而非假定其他车调位");
        var parkedVersion = new EngineeringVersion("parked-fixture", "draft", DateTimeOffset.UtcNow, "纯模型", constrained, EngineeringStore.Hash(constrained));
        var parkedRejected = false;
        try { EngineeringSimulator.Run(parkedVersion, new(parkedVersion.Id, [new("single", constrained.Stations[0].Id)], [], [])); }
        catch (ArgumentException) { parkedRejected = true; }
        check(parkedRejected, "原受限池单呼叫场景直接拒绝，不报告车辆重叠的最终位置");

        // With distinct external initial positions, all bays are actually vacant. A residual
        // augmenting path can safely relocate an assignment without moving any idle vehicle.
        constrained = FromIndependentStaging(constrained);
        check(EngineeringValidator.Validate(constrained).Valid, "多池重叠授权的全局可行匹配应通过发布校验");
        var full = Match(constrained);
        check(full.Assignments.Count == 3 && full.Unassigned.Count == 0, "最大匹配可重新分配，避免贪心有解却失败");
        check(full.Assignments[ids[0]].NodeId == constrained.Standbys[1].NodeId, "灵活车辆改到专属点，保留受限车辆可用点");
        Safe(constrained, full);

        var version = new EngineeringVersion("matching-fixture", "draft", DateTimeOffset.UtcNow, "纯模型", constrained, EngineeringStore.Hash(constrained));
        var report = EngineeringSimulator.Run(version, new(version.Id, constrained.Stations.Select(s => new EngineeringCall(s.Id, s.Id)).ToList(), [], []));
        check(report.Tasks.Count == 6 && report.Blocked.Count == 0, "模拟任务实际使用全局匹配待机分配");
        check(report.FinalPositions.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3, "模型完成后各车待机节点独立");
        check(report.EvidenceBoundary.Contains("纯配置") && report.EvidenceBoundary.Contains("未运行实车"), "待机结果不冒充实际导航或碰撞验收");

        var reverse = constrained with
        {
            Vehicles = constrained.Vehicles.AsEnumerable().Reverse().ToList(),
            Standbys = constrained.Standbys.AsEnumerable().Reverse().ToList(),
            Pools = constrained.Pools.AsEnumerable().Reverse().ToList()
        };
        check(Match(reverse).Assignments.OrderBy(a => a.Key).SequenceEqual(full.Assignments.OrderBy(a => a.Key)),
            "输入列表顺序变化不改变确定性的匹配结果");

        // Higher-priority bays cannot be borrowed from vehicles that never receive a job.
        var single = EngineeringSample.Create(6, 3);
        single = single with { Standbys = single.Standbys.Select((b, i) => b with { Priority = i == 0 ? 0 : i == 1 ? 100 : 90 }).ToList() };
        EngineeringSimulationReport OneCall(EngineeringPlan plan)
        {
            var v = new EngineeringVersion("single-fixture", "draft", DateTimeOffset.UtcNow, "纯模型", plan, EngineeringStore.Hash(plan));
            return EngineeringSimulator.Run(v, new(v.Id, [new("single", plan.Stations[0].Id)], [], []));
        }
        void IdlePositionsPreserved(EngineeringPlan plan, EngineeringSimulationReport one, string scenario)
        {
            check(one.Tasks.Count == 1 && one.Blocked.Count == 0, scenario + "完成一个模型任务");
            check(one.FinalPositions.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Vehicles.Count,
                scenario + "最终停车节点不重叠");
            foreach (var vehicle in plan.Vehicles.Where(v => one.Tasks.All(t => t.VehicleId != v.Id)))
                check(one.FinalPositions[vehicle.Id] == vehicle.StartNode, scenario + "未接任务车辆保留实际初始位置");
        }
        IdlePositionsPreserved(single, OneCall(single), "单呼叫且其他车辆在线空闲时");
        var lowBattery = single with { Vehicles = single.Vehicles.Select((v, i) => i == 0 ? v : v with { Battery = 5 }).ToList() };
        var lowRun = OneCall(lowBattery);
        IdlePositionsPreserved(lowBattery, lowRun, "低电量车辆原地占位时");
        check(lowRun.Tasks.Single().VehicleId == lowBattery.Vehicles[0].Id, "低电量车辆未被假设已接单或已调位");
        var incapable = single with { Vehicles = single.Vehicles.Select((v, i) => i == 0 ? v : v with { Capability = "Other" }).ToList() };
        IdlePositionsPreserved(incapable, OneCall(incapable), "无任务所需能力的车辆原地占位时");

        foreach (var size in new[] { 1, 4, 7, 13 })
        {
            var arbitrary = EngineeringSample.Create(size + 2, size);
            var matched = Match(arbitrary);
            check(matched.Assignments.Count == size, "待机匹配车辆数量来自配置，不限定两车或五车");
            Safe(arbitrary, matched);
        }

        var shared = FromIndependentStaging(EngineeringSample.Create(4, 3));
        shared = shared with
        {
            Resources = [..shared.Resources, new("SHARED-PARK", "两车共享待机资源", "Standby", 2)],
            Standbys = shared.Standbys.Select(b => b with { ResourceId = "SHARED-PARK" }).ToList()
        };
        var limited = Match(shared);
        check(limited.Assignments.Count == 2 && limited.Unassigned.Count == 1, "三个独立节点共享容量二时只分配两车");
        check(limited.Unassigned.Single().Value.Contains("容量") && limited.Unassigned.Keys.All(id => shared.Vehicles.Any(v => v.Id == id)),
            "容量不足直接列出具体未分配车辆及原因");
        check(EngineeringValidator.Validate(shared).Issues.Any(i => i.Section == "pools" && i.Message.Contains(limited.Unassigned.Single().Key)),
            "发布校验与模拟使用同一分配器并指出未分配车辆");
        Safe(shared, limited);

        var overOccupied = EngineeringSample.Create(4, 3);
        overOccupied = overOccupied with
        {
            Resources = [..overOccupied.Resources, new("INITIAL-SHARED", "初始共享待机资源", "Standby", 2)],
            Standbys = overOccupied.Standbys.Select(b => b with { ResourceId = "INITIAL-SHARED" }).ToList()
        };
        check(EngineeringValidator.Validate(overOccupied).Issues.Any(i => i.Section == "resources" && i.Message.Contains("初始停车")),
            "初始占位已超过共享容量时先拒绝，不靠未发生的调位腾容量");
        var partiallyOccupied = EngineeringSample.Create(4, 2);
        partiallyOccupied.Nodes.Add(new("EMPTY-PARK", "SIM-EMPTY-PARK", -6, -6));
        partiallyOccupied.Edges.Add(new("E-EMPTY-PARK", "EMPTY-PARK", "MAIN", 8, .5, []));
        partiallyOccupied.Resources.Add(new("PARK-SHARED", "共享待机容量", "Standby", 2));
        partiallyOccupied = partiallyOccupied with { Standbys = [..partiallyOccupied.Standbys.Select(b => b with { ResourceId = "PARK-SHARED" }), new("B-EMPTY", "EMPTY-PARK", "POOL-A", "PARK-SHARED", 100)] };
        var occupiedMatch = Match(partiallyOccupied);
        check(occupiedMatch.Assignments.Count == 2 && occupiedMatch.Assignments.Values.All(a => a.NodeId != "EMPTY-PARK"),
            "共享容量已被两车原停车占满时，不能再给空节点分配额外容量");
        IdlePositionsPreserved(partiallyOccupied, OneCall(partiallyOccupied), "共享资源中未接单车保留容量时");

        var inflated = EngineeringSample.Create(2, 3);
        inflated = inflated with
        {
            Standbys = [inflated.Standbys[0]],
            Resources = inflated.Resources.Select(r => r.Id == inflated.Standbys[0].ResourceId ? r with { Capacity = 3 } : r).ToList()
        };
        check(Match(inflated).Assignments.Count == 1, "资源容量三不能把一个物理节点当成三个停车点");
        check(!EngineeringValidator.Validate(inflated).Valid, "只有一个物理点时不能仅靠提高资源容量通过发布");

        var duplicateNode = EngineeringSample.Create(2, 2);
        duplicateNode = duplicateNode with { Standbys = duplicateNode.Standbys.Select(b => b with { NodeId = duplicateNode.Standbys[0].NodeId }).ToList() };
        check(EngineeringValidator.Validate(duplicateNode).Issues.Any(i => i.Message.Contains("重复配置")), "重复物理待机节点在匹配前被校验拒绝");
        var duplicateRejected = false;
        try { Match(duplicateNode); } catch (ArgumentException) { duplicateRejected = true; }
        check(duplicateRejected, "直接调用模型分配器也不能把重复物理节点当独立位置");

        var available = EngineeringSample.Create(5, 3);
        var offlineId = available.Vehicles[0].Id;
        var offlineNode = available.Vehicles[0].StartNode;
        var offline = Match(available, [offlineId]);
        check(offline.Assignments.Count == 2 && !offline.Assignments.ContainsKey(offlineId), "离线车辆不参与待机分配");
        check(offline.Assignments.Values.All(a => a.NodeId != offlineNode), "离线车辆原地保留，不能把其待机点分给在线车辆");
        check(offline.Unassigned[offlineId].Contains("离线"), "离线未分配原因明确");
        Safe(available, offline);
        var none = Match(available, blocked: available.Standbys.Select(b => b.ResourceId).ToArray());
        check(none.Assignments.Count == 0 && none.Unassigned.Count == 3 && none.Unassigned.Values.All(r => r.Contains("封锁")),
            "待机资源封锁时列出所有受影响车辆，未伪造可用待机点");

        var malformed = available with { Standbys = available.Standbys.Select(b => b with { ResourceId = "NO-RESOURCE" }).ToList() };
        check(!EngineeringValidator.Validate(malformed).Valid, "资源引用无效时先返回配置错误，不触发匹配器异常");
        var duplicateStart = available with { Vehicles = available.Vehicles.Select(v => v with { StartNode = available.Vehicles[0].StartNode }).ToList() };
        check(EngineeringValidator.Validate(duplicateStart).Issues.Any(i => i.Section == "vehicles" && i.Field == "startNode"),
            "多辆车初始位置重合必须明确配置错误，不能报告无冲突的待机模型");
    }
}
