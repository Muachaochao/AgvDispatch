using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.WebHost.Engineering;

public sealed record EngineeringRuntimePreview(string VersionId, string ContentHash, string MapId, string MapVersionId,
    string MapDefinitionHash, string ParameterRevision, string CommunicationRevision, bool MapActive,
    TaskParameterConfiguration? Configuration, IReadOnlyList<string> Blockers, IReadOnlyList<string> Notes)
{
    public bool Ready => Configuration is not null && Blockers.Count == 0;
}

// Compiles against a published authoritative map; never redraws vendor curves or overwrites communication.
public static class EngineeringRuntimeCompiler
{
    public static EngineeringRuntimePreview Compile(EngineeringVersion version, MapWorkspaceSnapshot workspace,
        TaskParameterSnapshot parameters, CommunicationSettingsSnapshot communication)
    {
        var plan = version.Plan;
        var errors = EngineeringValidator.Validate(plan).Issues.Where(i => i.Severity == "Error")
            .Select(i => i.ObjectId + "：" + i.Message).ToList();
        if (version.ContentHash != EngineeringStore.Hash(plan)) errors.Add("工程版本内容摘要不一致。");
        var map = workspace.Published;
        if (workspace.Project.MapId != plan.MapId || map is null || map.MapId != plan.MapId || map.VersionId != plan.MapVersion || map.Status != MapVersionStatus.Published)
            errors.Add("工程必须绑定目标地图当前已发布的准确版本；请先在地图编辑准备运行图并发布，再重新绑定工程版本。");
        var definition = map?.Definition;
        if (definition is not null)
        {
            if (definition.CoordinateSystem != "RobotWorld" || definition.Unit != "m") errors.Add("目标地图尚未准备为米制 RobotWorld 运行地图。");
            // Compare the full topology, not only site labels. Geometry remains in the original map version.
            if (plan.Nodes.Count != definition.Nodes.Count || plan.Edges.Count != definition.Edges.Count || plan.Resources.Count != definition.Resources.Count)
                errors.Add("工程拓扑数量与发布地图不同；重新读取准确地图拓扑，禁止用模拟拓扑激活现场地图。");
            foreach (var node in plan.Nodes)
            {
                var actual = definition.Nodes.SingleOrDefault(n => n.Id == node.Id);
                if (actual is null || actual.AgvSiteId != node.VendorSite || actual.Enabled != node.Enabled || Math.Abs(actual.X-node.X)>1e-6 || Math.Abs(actual.Y-node.Y)>1e-6)
                    errors.Add("站点坐标、厂家编号或启用状态与发布地图不同：" + node.Id);
            }
            foreach (var edge in plan.Edges)
            {
                var actual = definition.Edges.SingleOrDefault(e => e.Id == edge.Id);
                if (actual is null || actual.From != edge.From || actual.To != edge.To || actual.Enabled != edge.Enabled || actual.Bidirectional != edge.Bidirectional ||
                    Math.Abs(actual.Distance-edge.Distance)>1e-6 || Math.Abs(actual.SpeedLimit-edge.Speed)>1e-6 || !actual.ResourceIds.Order().SequenceEqual(edge.ResourceIds.Order()))
                    errors.Add("道路连接、方向、限速或资源与发布地图不同：" + edge.Id);
            }
            foreach (var resource in plan.Resources)
                if (definition.Resources.SingleOrDefault(r => r.Id == resource.Id)?.Capacity != resource.Capacity)
                    errors.Add("资源容量与发布地图不同：" + resource.Id);
            var stops = plan.Stations.Where(s => s.Enabled).Select(s => s.PickupNode)
                .Concat(plan.Dumpers.SelectMany(d => new[] { d.WaitingNode, d.DumpingNode })).Concat(plan.Standbys.Select(b => b.NodeId));
            foreach (var id in stops.Distinct())
                if (definition.Nodes.SingleOrDefault(n => n.Id == id) is not { Enabled: true, Type: MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint })
                    errors.Add("业务停靠点未设置有效停靠用途：" + id);
        }
        var stations = plan.Stations.Where(s => s.Enabled).ToArray();
        var dumpers = stations.Select(s => s.DumperId).Distinct().ToArray();
        if (dumpers.Length != 1) errors.Add("当前九步执行器一次工程应用只支持一组倒料设备；多倒料工程暂不能应用，未静默选择第一组。");
        if (stations.Select(s => s.TemplateId).Distinct().Count() != 1) errors.Add("当前执行器共用四段动作；不同业务模板不能合并应用。");
        if (communication.RestartRequired) errors.Add("通信保存配置尚未重启生效，请先完成受控重启并重新预检。");
        foreach (var plc in plan.Plcs.Where(p => p.Enabled))
            if (!communication.Active.Plcs.Any(p => p.Enabled && p.DeviceId == plc.Id && p.IpAddress == plc.Host && p.Port == plc.Port))
                errors.Add("PLC 连接未在当前后台生效或不匹配：" + plc.Id);
        foreach (var vehicle in plan.Vehicles.Where(v => v.Enabled))
            if (!communication.Active.Agvs.Any(v => v.Enabled && v.VehicleId == vehicle.Id && v.IpAddress == vehicle.Host))
                errors.Add("车辆连接未在当前后台生效或不匹配：" + vehicle.Id);
        var active = parameters.MapId == plan.MapId && parameters.MapVersionId == plan.MapVersion;
        TaskParameterConfiguration? configuration = null;
        if (errors.Count == 0 && definition is not null)
        {
            var dumper = plan.Dumpers.Single(d => d.Id == dumpers[0]);
            configuration = parameters.Configuration with
            {
                DumperStationId = dumper.Id, WaitingSite = dumper.WaitingNode, DumpingSite = dumper.DumpingNode,
                Pickups = stations.Select(s => new TaskPickupParameter(s.Id, s.PickupNode)
                {
                    VehiclePoolId = s.PoolId,
                    AllowedVehicleIds = plan.Pools.Single(p => p.Id == s.PoolId).VehicleIds
                        .Where(id => plan.Vehicles.Any(v => v.Id == id && v.Enabled && v.Capability == plan.Templates.Single(t => t.Id == s.TemplateId).Capability)).ToArray()
                }).ToArray(),
                EngineeringSource = new(version.Id, version.ContentHash, EngineeringRuntimeIdentity.MapHash(definition))
            };
            if (configuration.Pickups.Any(p => p.AllowedVehicleIds!.Count == 0)) { errors.Add("至少一个工位没有启用且能力匹配的池内车辆。"); configuration = null; }
        }
        return new(version.Id, version.ContentHash, plan.MapId, plan.MapVersion, definition is null ? "" : EngineeringRuntimeIdentity.MapHash(definition),
            parameters.Revision, communication.Revision, active, configuration, errors,
            ["使用后台原发布地图，保留圆弧、贝塞尔、方向和厂家来源证据；此步骤不上传机器人地图。",
             "四段车辆动作和九步超时沿用当前后台参数，须在应用前核对；规划模板耗时不转换为车辆动作。",
             "现场 PLC 点表及已核定出区包络继续由真实执行器逐步检查；工程候选包络不自动成为现场标定。",
             "应用只保存执行参数，不启动任务、不开放 PLC/车辆写许可，也不解除地图切换或启动恢复屏障。"]);
    }
}
