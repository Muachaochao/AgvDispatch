namespace AgvDispatch.WebHost.Engineering;

public static class EngineeringSample
{
    public static EngineeringPlan Create(int count, int vehicleCount)
    {
        if (count is < 1 or > 1000 || vehicleCount is < 1 or > 20) throw new ArgumentException("示例工位1–1000、车辆1–20。");
        var p = new EngineeringPlan { Name = $"隔离示例 · {count}工位 / {vehicleCount}车", MapId = "SIM-FACTORY", MapVersion = "SIM-1" };
        p.Plcs.Add(new("PLC-SIM", "simulation.invalid", 10200));
        p.Templates.Add(EngineeringTaskTemplates.Create("BOX-CYCLE", "标准取箱倒料返箱", "Jack"));
        p.Nodes.Add(new("MAIN", "SIM-MAIN", 0, 0));
        p.Nodes.Add(new("WAIT", "SIM-WAIT", 10, 0)); p.Nodes.Add(new("DUMP", "SIM-DUMP", 12, 0));
        p.Resources.Add(new("WAIT-BAY", "倒料等待位", "Waiting")); p.Resources.Add(new("DUMP-AREA", "倒料区域", "Dumper"));
        p.Edges.Add(new("MAIN-WAIT", "MAIN", "WAIT", 10, .5, [])); p.Edges.Add(new("WAIT-DUMP", "WAIT", "DUMP", 2, .3, []));
        p.Dumpers.Add(new("DUMPER-001", "共享倒料设备", "PLC-SIM", "WAIT", "DUMP", "WAIT-BAY", "DUMP-AREA"));
        void Signals(string owner, int bytes, bool dumper)
        {
            var names = dumper ? EngineeringValidator.DumperInputs.Concat(EngineeringValidator.DumperOutputs) : EngineeringValidator.StationInputs.Concat(EngineeringValidator.StationOutputs);
            var i = 0;
            foreach (var name in names)
            {
                var read = (dumper ? EngineeringValidator.DumperInputs : EngineeringValidator.StationInputs).Contains(name);
                p.Signals.Add(new(owner + "-" + name, "PLC-SIM", owner, name, $"DB1.DBX{bytes + i / 8}.{i % 8}", read ? "Read" : "Write")); i++;
            }
        }
        Signals("DUMPER-001", 4000, true);
        var ids = Enumerable.Range(1, vehicleCount).Select(i => $"AGV-{i:00}").ToArray();
        p.Pools.Add(new("POOL-A", "全厂服务车辆池", ids));
        for (var i = 1; i <= vehicleCount; i++)
        {
            var node = $"PARK-{i:00}";
            p.Nodes.Add(new(node, "SIM-" + node, -3, -i * 2)); p.Edges.Add(new("E-" + node, node, "MAIN", 3 + i, .5, []));
            p.Resources.Add(new("R-" + node, "独立待机位" + i, "Standby")); p.Standbys.Add(new("B-" + node, node, "POOL-A", "R-" + node));
            p.Vehicles.Add(new($"AGV-{i:00}", "模拟车" + i, "simulation.invalid", node, "Jack", .49, .1, "模拟参数，未用于现场核定", 80));
        }
        for (var i = 1; i <= count; i++)
        {
            var id = $"CNC-{i:000}"; var node = $"BUS-{i:000}";
            p.Nodes.Add(new(node, $"SIM-LM{i}", (i % 30) * 3, (i / 30 + 1) * 4));
            p.Edges.Add(new("E-" + node, "MAIN", node, 5 + i % 30, .5, []));
            p.Stations.Add(new(id, "取放箱工位" + i, "区域" + ((i - 1) / 50 + 1), node, "PLC-SIM", "BOX-CYCLE", "POOL-A", "DUMPER-001")); Signals(id, i * 2, false);
        }
        return p;
    }
}
