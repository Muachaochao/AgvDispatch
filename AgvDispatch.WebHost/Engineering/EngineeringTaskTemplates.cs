using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost.Engineering;

public static partial class EngineeringTaskTemplates
{
    public const string DurableNineStepEngine = "DurableNineStepV1";
    public const string FromStation = "fromStation";
    public const string WaitingSite = "waitingSite";
    public const string ToStation = "toStation";
    public const string VehiclePool = "vehiclePool";
    public const string VehicleIds = "vehicleIds";
    public const string DependsOnTaskIds = "dependsOnTaskIds";

    private static readonly string[] RequiredInputKeys = [FromStation, WaitingSite, ToStation, VehiclePool, VehicleIds];
    private static readonly string[] SupportedInputKeys = [..RequiredInputKeys, DependsOnTaskIds];
    private static readonly string[] RequiredOutputKeys = ["vehicleId", "arrival", "pickConfirmed", "dumpConfirmed", "taskCompleted", "error"];
    private static readonly string[] ExpectedBlocks =
    [
        "SelectAgv", "AcquireTraffic", "GoToPickup", "PickupFullBox", "DeliverFullBoxToDumper",
        "WaitForDumperReady", "EnterDumpingPosition", "DumpFullBox", "ReturnEmptyBox", "PlaceEmptyBox",
        "CompletePlcHandshake", "ReleaseTraffic"
    ];
    private static readonly string[] LegacyExecutableBlocks =
    [
        "SelectAgv", "GoToPickup", "PickupFullBox", "DeliverFullBoxToDumper", "WaitForDumperReady",
        "EnterDumpingPosition", "DumpFullBox", "ReturnEmptyBox", "PlaceEmptyBox", "CompletePlcHandshake"
    ];
    private static readonly string[] FailurePolicies = ["HoldUntilCondition", "Pause", "ManualRecovery", "Terminate"];
    private static readonly string[] TimeoutActions = ["NotifyAndHold", "Pause", "ManualRecovery", "Terminate"];
    private static readonly HashSet<string> MechanicalBlocks =
    [
        "AcquireTraffic", "GoToPickup", "PickupFullBox", "DeliverFullBoxToDumper", "EnterDumpingPosition",
        "DumpFullBox", "ReturnEmptyBox", "PlaceEmptyBox", "CompletePlcHandshake", "ReleaseTraffic"
    ];

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Identifier();

    public static EngineeringTemplate Create(string id, string name, string capability,
        double pickupSeconds = 5, double dumpSeconds = 10, double returnSeconds = 5, int version = 1) =>
        new(id, name, capability, pickupSeconds, dumpSeconds, returnSeconds)
        {
            Version = version,
            Engine = DurableNineStepEngine,
            Inputs =
            [
                new(FromStation, "取放箱站点", "string", true, "station.pickupNode"),
                new(WaitingSite, "候车站点列表", "string[]", true, "dumper.waitingNodes"),
                new(ToStation, "倒料站点", "string", true, "dumper.dumpingNode"),
                new(VehiclePool, "车辆池", "string", true, "station.poolId"),
                new(VehicleIds, "车辆池成员", "string[]", true, "pool.vehicleIds"),
                new(DependsOnTaskIds, "前置任务", "string[]", false, "request.dependsOnTaskIds")
            ],
            Outputs =
            [
                new("vehicleId", "执行车辆", "string", false, "SelectAgv.vehicleId"),
                new("arrival", "取货到点", "bool", false, "GoToPickup.arrival"),
                new("pickConfirmed", "取货确认", "bool", false, "PickupFullBox.pickConfirmed"),
                new("dumpConfirmed", "倒料确认", "bool", false, "DumpFullBox.dumpConfirmed"),
                new("taskCompleted", "任务完成", "bool", false, "CompletePlcHandshake.taskCompleted"),
                new("error", "失败原因", "string", false, "any.error")
            ],
            Variables = DefaultVariables(),
            Nodes = DefaultNodes(),
            Connections = BuildConnections(DefaultNodes(), DefaultVariables()),
            Guards = DefaultGuards()
        };

    public static List<EngineeringTemplateVariable> DefaultVariables() =>
    [
        new(FromStation, "取放箱站点", "string", "external"),
        new(WaitingSite, "候车站点列表", "string[]", "external"),
        new(ToStation, "倒料站点", "string", "external"),
        new(VehiclePool, "车辆池", "string", "external"),
        new(VehicleIds, "车辆池成员", "string[]", "external"),
        new(DependsOnTaskIds, "前置任务", "string[]", "external"),
        new("vehicleId", "执行车辆", "string", "select-agv"),
        new("arrival", "取货到点", "bool", "go-to-pickup"),
        new("pickConfirmed", "取货确认", "bool", "pickup-full-box"),
        new("waitingArrival", "候车到点", "bool", "deliver-to-waiting"),
        new("dumperReady", "倒料机就绪", "bool", "wait-dumper-ready"),
        new("dumpingArrival", "倒料位到点", "bool", "enter-dumping"),
        new("dumpConfirmed", "倒料确认", "bool", "dump-full-box"),
        new("returned", "返箱到点", "bool", "return-empty-box"),
        new("placeConfirmed", "空箱放置确认", "bool", "place-empty-box"),
        new("taskCompleted", "任务完成", "bool", "complete-handshake"),
        new("error", "失败原因", "string", "any")
    ];

    public static List<EngineeringTemplateGuard> DefaultGuards() =>
    [
        new("guard-vehicle", "VehicleUnavailable", "HoldUntilAvailable", "select-agv", "无可用车辆时保留任务，等待车辆恢复，不换单或跳步。"),
        new("guard-route", "RouteUnavailable", "HoldAndReplan", "go-to-pickup", "路线不可达时保留任务与责任，等待道路或地图条件恢复。"),
        new("guard-plc", "PlcSignalNotReady", "HoldUntilCondition", "wait-dumper-ready", "PLC未满足条件时保持原步骤，不提前进入下一节点。"),
        new("guard-traffic", "TrafficResourceOccupied", "HoldForResource", "deliver-to-waiting", "路权或候车位被占用时保留原任务并等待资源释放。"),
        new("guard-handshake", "HandshakeFaulted", "EnterManualRecovery", "complete-handshake", "握手异常时保留设备和任务责任，进入人工恢复核对。")
    ];

    public static List<EngineeringTemplateNode> DefaultNodes() =>
    [
        new("select-agv", "SelectAgv", "选择车辆", [VehiclePool, VehicleIds], ["vehicleId"],
            "车辆在线、定位新鲜、能力匹配，并存在到取货站点的可执行路线")
            { FailurePolicy = "HoldUntilCondition", TimeoutAction = "NotifyAndHold" },
        new("acquire-traffic", "AcquireTraffic", "取得首段路权", ["vehicleId", FromStation], [],
            "通过现有 DispatchTrafficReservationService 申请整组路线资源；本适配节点不发送车辆命令")
            { FailurePolicy = "HoldUntilCondition", TimeoutAction = "NotifyAndHold" },
        new("go-to-pickup", "GoToPickup", "移动至取货点", ["vehicleId", FromStation], ["arrival"],
            "持有效交通路权；车辆到点且厂家导航任务完成")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("pickup-full-box", "PickupFullBox", "取满箱", ["arrival", FromStation], ["pickConfirmed"],
            "PLC质量Good；BoxPresent=false；FullBoxRemoved=true；EmptyBoxTaskAcknowledged=true")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("deliver-to-waiting", "DeliverFullBoxToDumper", "送满箱至候车点", ["pickConfirmed", FromStation, WaitingSite], ["waitingArrival"],
            "候车位按任务持久分配；取得路权后导航完成")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("wait-dumper-ready", "WaitForDumperReady", "等待倒料机就绪", ["waitingArrival", WaitingSite], ["dumperReady"],
            "Faulted=false；AgvAtWaitingPosition=true；DumperReady=true，全部要求Good质量")
            { FailurePolicy = "HoldUntilCondition", TimeoutAction = "NotifyAndHold" },
        new("enter-dumping", "EnterDumpingPosition", "进入倒料位", ["dumperReady", WaitingSite, ToStation], ["dumpingArrival"],
            "倒料设备独占责任已交接；进入倒料位导航完成")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("dump-full-box", "DumpFullBox", "等待倒料完成", ["dumpingArrival", ToStation], ["dumpConfirmed"],
            "Faulted=false；AgvAtDumpingPosition=true；DumpingCompletedMayLeave=true")
            { FailurePolicy = "HoldUntilCondition", TimeoutAction = "NotifyAndHold" },
        new("return-empty-box", "ReturnEmptyBox", "返空箱至取放点", ["dumpConfirmed", ToStation, FromStation], ["returned"],
            "实际出区核验通过后释放已验证区域，保留设备与返箱责任")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("place-empty-box", "PlaceEmptyBox", "放置空箱", ["returned", FromStation], ["placeConfirmed"],
            "EmptyBoxPlaced=true；BoxPresent=true，全部要求Good质量")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("complete-handshake", "CompletePlcHandshake", "结束PLC握手", ["placeConfirmed"], ["taskCompleted"],
            "按28阶段顺序复位工位PLC信号；只有任务、路权和证据闭合后完成")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" },
        new("release-traffic", "ReleaseTraffic", "释放本任务路权", ["taskCompleted"], [],
            "只释放现有交通生命周期已证明可释放的资源；设备、PLC周期和返箱责任按本任务记录保持")
            { FailurePolicy = "ManualRecovery", TimeoutAction = "ManualRecovery" }
    ];

    public static EngineeringTemplate Normalize(EngineeringTemplate template)
    {
        var defaults = Create(template.Id, template.Name, template.Capability, template.PickupSeconds,
            template.DumpSeconds, template.ReturnSeconds, template.Version);
        var variables = template.Variables is { Count: > 0 } ? template.Variables : defaults.Variables;
        var nodes = NormalizeNodes(template.Nodes is { Count: > 0 } ? template.Nodes : defaults.Nodes, defaults.Nodes);
        return template with
        {
            Engine = string.IsNullOrWhiteSpace(template.Engine) ? DurableNineStepEngine : template.Engine,
            Inputs = template.Inputs is { Count: > 0 } ? template.Inputs : defaults.Inputs,
            Outputs = template.Outputs is { Count: > 0 } ? template.Outputs : defaults.Outputs,
            Variables = variables,
            Nodes = nodes,
            Connections = BuildConnections(nodes, variables),
            Guards = template.Guards is { Count: > 0 } ? template.Guards : defaults.Guards,
            SourceTemplateId = template.SourceTemplateId ?? ""
        };
    }

    private static List<EngineeringTemplateNode> NormalizeNodes(
        IReadOnlyList<EngineeringTemplateNode> nodes,
        IReadOnlyList<EngineeringTemplateNode> defaults)
    {
        if (nodes.Count == LegacyExecutableBlocks.Length &&
            nodes.Select(node => node.Block).SequenceEqual(LegacyExecutableBlocks))
        {
            var acquire = defaults.Single(node => node.Block == "AcquireTraffic");
            var release = defaults.Single(node => node.Block == "ReleaseTraffic");
            return nodes.Take(1).Append(acquire).Concat(nodes.Skip(1)).Append(release).ToList();
        }
        return nodes.ToList();
    }

    public static List<EngineeringTemplateConnection> BuildConnections(
        IReadOnlyList<EngineeringTemplateNode> nodes, IReadOnlyList<EngineeringTemplateVariable> variables)
    {
        var producers = variables.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var connections = new List<EngineeringTemplateConnection>();
        foreach (var node in nodes)
            foreach (var input in node.Inputs)
            {
                if (!producers.TryGetValue(input, out var variable)) continue;
                connections.Add(new(variable.Producer, variable.Key, node.Id, input, variable.Type,
                    variable.Producer.Equals("external", StringComparison.OrdinalIgnoreCase) ||
                    variable.Producer.Equals("any", StringComparison.OrdinalIgnoreCase)));
            }
        return connections;
    }

    public static string Hash(EngineeringTemplate template) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Normalize(template), WebJson.Options)));

    public static TaskTemplateBinding ToRuntimeBinding(EngineeringTemplate template)
    {
        var normalized = Normalize(template);
        return new(normalized.Id, normalized.Version, Hash(normalized), normalized.Engine,
            normalized.Inputs.Select(x => new TaskTemplatePortBinding(x.Key, x.Label, x.Type, x.Required, x.Source)).ToArray(),
            normalized.Outputs.Select(x => new TaskTemplatePortBinding(x.Key, x.Label, x.Type, x.Required, x.Source)).ToArray(),
            normalized.Variables.Select(x => new TaskTemplateVariableBinding(x.Key, x.Label, x.Type, x.Producer)).ToArray(),
            normalized.Nodes.Select(x => new TaskTemplateNodeBinding(x.Id, x.Block, x.Name, x.Inputs, x.Outputs,
                x.Condition, x.FailurePolicy, x.TimeoutAction, x.TimeoutSeconds, x.AutoRetry)).ToArray(),
            normalized.Connections.Select(x => new TaskTemplateConnectionBinding(x.FromNodeId, x.Output, x.ToNodeId,
                x.Input, x.Type, x.External)).ToArray(),
            normalized.Guards.Select(x => new TaskTemplateGuardBinding(x.Id, x.Trigger, x.Action, x.TargetNodeId, x.Description)).ToArray());
    }

    public static IReadOnlyList<(string Field, string Severity, string Message)> Validate(EngineeringTemplate template)
    {
        var issues = new List<(string, string, string)>();
        void Add(string field, string message, string severity = "Error") => issues.Add((field, severity, message));
        if (!Identifier().IsMatch(template.Id ?? "")) Add("id", "模板编号仅支持1–64位字母、数字、下划线、连字符。");
        if (string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 80) Add("name", "模板名称应为1–80字符。");
        if (string.IsNullOrWhiteSpace(template.Capability)) Add("capability", "车辆能力不能为空。");
        if (template.Version is < 1 or > 1_000_000) Add("version", "模板版本应为1–1000000。");
        if (!string.Equals(template.Engine, DurableNineStepEngine, StringComparison.Ordinal))
            Add("engine", $"当前仅支持 {DurableNineStepEngine} 模板执行器。");
        if (template.SourceVersion is < 0 or > 1_000_000) Add("sourceVersion", "来源模板版本无效。");
        var sourceTemplateId = template.SourceTemplateId ?? "";
        if (sourceTemplateId.Length > 64 || sourceTemplateId.Length > 0 && !Identifier().IsMatch(sourceTemplateId))
            Add("sourceTemplateId", "来源模板编号无效。");
        if (new[] { template.PickupSeconds, template.DumpSeconds, template.ReturnSeconds }.Any(x => !double.IsFinite(x) || x <= 0 || x > 3600))
            Add("capability", "任务模板模拟动作耗时无效。");

        if (template.Inputs is { Count: > 0 })
        {
            if (!template.Inputs.Select(x => x.Key).Order().SequenceEqual(SupportedInputKeys.Order()))
                Add("inputs", "模板输入必须包含固定的取货、候车、倒料、车辆池、车辆成员和可选前置任务绑定。");
            foreach (var input in template.Inputs)
                if (input is null || string.IsNullOrWhiteSpace(input.Key) || input.Type is not ("string" or "string[]"))
                    Add("inputs", "模板输入键或类型无效。");
        }
        else Add("inputs", "旧模板未保存节点定义；预览将使用内置九步契约，重新保存模板库后固化。", "Warning");

        if (template.Outputs is { Count: > 0 })
        {
            if (!template.Outputs.Select(x => x.Key).Order().SequenceEqual(RequiredOutputKeys.Order()))
                Add("outputs", "模板输出必须包含车辆、到点、取货、倒料、完成和错误变量。");
            foreach (var output in template.Outputs)
                if (output is null || string.IsNullOrWhiteSpace(output.Key) || output.Type is not ("string" or "string[]" or "bool"))
                    Add("outputs", "模板输出键或类型无效。");
        }
        else Add("outputs", "旧模板未保存输出定义；预览将使用内置输出契约，重新保存模板库后固化。", "Warning");

        var nodes = template.Nodes ?? [];
        var validNodes = nodes.Where(x => x is not null).ToArray();
        var variables = template.Variables is { Count: > 0 } ? template.Variables : DefaultVariables();
        if (variables.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variables.Count)
            Add("variables", "模板变量键不能重复。");
        foreach (var variable in variables)
            if (variable is null || string.IsNullOrWhiteSpace(variable.Key) ||
                variable.Type is not ("string" or "string[]" or "bool" or "int" or "double"))
                Add("variables", "模板变量键或类型无效。");
        foreach (var group in variables.Where(x => x.Producer is not null and not ("external" or "any"))
                     .GroupBy(x => x.Producer, StringComparer.OrdinalIgnoreCase))
            if (!validNodes.Any(x => x.Id.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                Add("variables", $"变量生产节点 {group.Key} 不存在。");

        if (template.Nodes is { Count: > 0 })
        {
            if (nodes.Any(x => x is null)) Add("nodes", "模板节点不能为null。");
            if (nodes.Count != ExpectedBlocks.Length)
                Add("nodes", $"九步模板必须包含{ExpectedBlocks.Length}个受控节点。");
            if (!validNodes.Select(x => x.Block).SequenceEqual(ExpectedBlocks))
                Add("nodes", "模板节点类型或顺序与 DurableNineStepV1 执行器不一致。");
            if (validNodes.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != validNodes.Length)
                Add("nodes", "模板节点编号不能重复。");
            foreach (var node in validNodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Name) ||
                    node.Inputs is null || node.Outputs is null || string.IsNullOrWhiteSpace(node.Condition))
                    Add("nodes", "模板节点定义不完整。");
                if (!FailurePolicies.Contains(node.FailurePolicy)) Add("nodes", $"节点 {node.Id} 的失败策略无效。");
                if (!TimeoutActions.Contains(node.TimeoutAction)) Add("nodes", $"节点 {node.Id} 的超时处置无效。");
                if (node.TimeoutSeconds is < 0 or > 604800) Add("nodes", $"节点 {node.Id} 的超时阈值应为0–604800秒。");
                if (node.AutoRetry && MechanicalBlocks.Contains(node.Block))
                    Add("nodes", $"节点 {node.Id} 是机械、交通或握手动作节点，禁止自动重试。");
                var inputs = node.Inputs ?? [];
                var outputs = node.Outputs ?? [];
                foreach (var input in inputs)
                    if (!variables.Any(x => string.Equals(x.Key, input, StringComparison.OrdinalIgnoreCase)))
                        Add("connections", $"节点 {node.Id} 的输入 {input} 没有类型定义。");
                foreach (var output in outputs)
                {
                    var produced = variables.Where(x => string.Equals(x.Key, output, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (produced.Length != 1)
                        Add("connections", $"节点 {node.Id} 的输出 {output} 必须对应唯一类型变量。");
                    else if (!produced[0].Producer.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                        Add("connections", $"输出 {output} 只能由声明节点 {produced[0].Producer} 写入。");
                }
            }
            var producedKeys = validNodes.SelectMany(x => x.Outputs ?? []).ToArray();
            if (producedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != producedKeys.Length)
                Add("connections", "同一输出变量只能有一个节点写入，不能依赖最后写入覆盖。");
        }
        else Add("nodes", "旧模板未保存节点定义；预览将使用内置九步契约，重新保存模板库后固化。", "Warning");

        if (template.Guards is { Count: > 0 })
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "VehicleUnavailable:HoldUntilAvailable", "RouteUnavailable:HoldAndReplan",
                "PlcSignalNotReady:HoldUntilCondition", "TrafficResourceOccupied:HoldForResource",
                "HandshakeFaulted:EnterManualRecovery"
            };
            if (template.Guards.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != template.Guards.Count)
                Add("guards", "异常分支编号不能重复。");
            foreach (var guard in template.Guards)
            {
                if (guard is null || !allowed.Contains(guard.Trigger + ":" + guard.Action))
                    Add("guards", "异常触发与处置组合不在白名单内。");
                else if (!validNodes.Any(x => x.Id.Equals(guard.TargetNodeId, StringComparison.OrdinalIgnoreCase)))
                    Add("guards", $"异常分支 {guard.Id} 的目标节点不存在。");
            }
        }
        else Add("guards", "旧模板未保存异常分支策略；预览将使用安全默认策略。", "Warning");
        return issues;
    }

    public static EngineeringTaskPreview Preview(EngineeringPlan plan, string stationId, string? templateId)
    {
        var issues = new List<EngineeringIssue>();
        var station = plan.Stations.FirstOrDefault(x => x.Id.Equals(stationId, StringComparison.OrdinalIgnoreCase));
        if (station is null)
        {
            return new("", 0, "", "", stationId, "", "", "", [], [], "", false, [], [], [],
                [new("Error", "stations", stationId, "id", "工位不存在。")]);
        }
        var selectedId = string.IsNullOrWhiteSpace(templateId) ? station.TemplateId : templateId;
        var sourceTemplate = plan.Templates.FirstOrDefault(x => x.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        if (sourceTemplate is null)
        {
            issues.Add(new("Error", "templates", selectedId, "id", "工位绑定的任务模板不存在。"));
            return new(selectedId, 0, "", "", station.Id, station.Name, station.PickupNode, station.PoolId, [], [], "", false,
                [], [], [], issues);
        }
        var template = Normalize(sourceTemplate);
        foreach (var contractIssue in Validate(template))
            if (contractIssue.Severity == "Error")
                issues.Add(new("Error", "templates", template.Id, contractIssue.Field, contractIssue.Message));
        var dumper = plan.Dumpers.FirstOrDefault(x => x.Id.Equals(station.DumperId, StringComparison.OrdinalIgnoreCase));
        if (dumper is null) issues.Add(new("Error", "dumpers", station.DumperId, "dumperId", "工位绑定的倒料设备不存在。"));
        var pool = plan.Pools.FirstOrDefault(x => x.Id.Equals(station.PoolId, StringComparison.OrdinalIgnoreCase));
        if (pool is null) issues.Add(new("Error", "pools", station.PoolId, "poolId", "工位绑定的车辆池不存在。"));
        var waitingSites = dumper is null ? [] : new[] { dumper.WaitingNode }.Concat(dumper.AdditionalWaitingNodes)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var vehicleIds = pool?.VehicleIds ?? [];
        var inputs = new[]
        {
            new EngineeringTaskPreviewInput(FromStation, "取放箱站点", "string", station.PickupNode, "工位取放箱节点", true),
            new EngineeringTaskPreviewInput(WaitingSite, "候车站点列表", "string[]", string.Join("、", waitingSites), "倒料设备主候车位 + 附加候车位", true),
            new EngineeringTaskPreviewInput(ToStation, "倒料站点", "string", dumper?.DumpingNode, "倒料设备倒料位", true),
            new EngineeringTaskPreviewInput(VehiclePool, "车辆池", "string", station.PoolId, "工位车辆池", true),
            new EngineeringTaskPreviewInput(VehicleIds, "车辆池成员", "string[]", string.Join("、", vehicleIds), "车辆池授权车辆", true),
            new EngineeringTaskPreviewInput(DependsOnTaskIds, "前置任务", "string[]", null, "任务创建请求可选输入", false)
        };
        var outputs = new[]
        {
            new EngineeringTaskPreviewInput("vehicleId", "执行车辆", "string", null, "SelectAgv.vehicleId", false),
            new EngineeringTaskPreviewInput("arrival", "取货到点", "bool", null, "GoToPickup.arrival", false),
            new EngineeringTaskPreviewInput("pickConfirmed", "取货确认", "bool", null, "PickupFullBox.pickConfirmed", false),
            new EngineeringTaskPreviewInput("dumpConfirmed", "倒料确认", "bool", null, "DumpFullBox.dumpConfirmed", false),
            new EngineeringTaskPreviewInput("taskCompleted", "任务完成", "bool", null, "CompletePlcHandshake.taskCompleted", false),
            new EngineeringTaskPreviewInput("error", "失败原因", "string", null, "any.error", false)
        };
        var pickup = station.PickupNode;
        var waiting = waitingSites.Length == 0 ? "" : string.Join(" / ", waitingSites);
        var dumping = dumper?.DumpingNode ?? "";
        var details = new Dictionary<string, (string Source, string Destination, string Action)>
        {
            ["SelectAgv"] = ("", "", "按车辆池、能力、电量、定位和路线选择车辆"),
            ["AcquireTraffic"] = ("车辆当前站点", pickup, "调用现有交通层申请首段路权；不发送独立车辆命令"),
            ["GoToPickup"] = ("车辆当前站点", pickup, "导航 + 第1步车辆附加动作"),
            ["PickupFullBox"] = (pickup, pickup, "无独立车辆命令；核对PLC取箱条件"),
            ["DeliverFullBoxToDumper"] = (pickup, waiting, "导航至持久分配的候车位"),
            ["WaitForDumperReady"] = (waiting, waiting, "无独立车辆命令；握手等待"),
            ["EnterDumpingPosition"] = (waiting, dumping, "导航进入倒料位"),
            ["DumpFullBox"] = (dumping, dumping, "无独立车辆命令；等待倒料完成"),
            ["ReturnEmptyBox"] = (dumping, pickup, "导航 + 第7步车辆附加动作"),
            ["PlaceEmptyBox"] = (pickup, pickup, "无独立车辆命令；核对空箱放置"),
            ["CompletePlcHandshake"] = (pickup, "", "无独立车辆命令；PLC复位并闭环"),
            ["ReleaseTraffic"] = (pickup, "", "调用现有交通生命周期释放已证明可释放的资源；不伪造设备交接")
        };
        var nodes = template.Nodes.Select((node, index) =>
        {
            var detail = details[node.Block];
            return new EngineeringTaskPreviewNode(index + 1, node.Id, node.Block, node.Name, detail.Source,
                detail.Destination, detail.Action, node.Condition, node.Inputs, node.Outputs)
            {
                FailurePolicy = node.FailurePolicy,
                TimeoutAction = node.TimeoutAction,
                TimeoutSeconds = node.TimeoutSeconds,
                AutoRetry = node.AutoRetry
            };
        }).ToArray();
        return new(template.Id, template.Version, Hash(template), template.Engine, station.Id, station.Name,
            station.PickupNode, station.PoolId, vehicleIds, waitingSites, dumping, issues.Count == 0,
            inputs, outputs, nodes, issues)
        {
            Connections = template.Connections,
            Guards = template.Guards
        };
    }
}
