namespace AgvDispatch.Connectors.Agv;

public sealed class AgvOptions
{
    public const string SectionName = "Agv";

    public bool Enabled { get; set; }
    public bool AllowCommands { get; set; }
    public string NavigationCommandMode { get; set; } = "SpecifiedPath";
    public bool SmartCancel { get; set; } = true;
    public int CommandConfirmationAttempts { get; set; } = 3;
    public int CommandConfirmationDelayMs { get; set; } = 300;
    public int PollIntervalMs { get; set; } = 500;
    public int ReconnectDelayMs { get; set; } = 3000;
    public int StaleAfterMs { get; set; } = 5000;
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int RequestTimeoutMs { get; set; } = 3000;
    public int MaximumFrameBytes { get; set; } = 4 * 1024 * 1024;
    public int RequestIntervalMs { get; set; } = 100;
    public byte ProtocolVersion { get; set; } = 1;
    // Full is the production default and keeps the per-edge 1303 vehicle-map
    // verification. SimulationLocal is only selected by the isolated simulator
    // whose virtual vehicle is loaded from the same authored map file.
    public string TaskMapPreflightMode { get; set; } = "Full";
    public List<RobokitMapBinding> MapBindings { get; set; } = [];
    public List<AgvRobotOptions> Robots { get; set; } = [];

    public string? GetConfigurationError()
    {
        if (!Enabled)
        {
            return null;
        }

        if (PollIntervalMs < 200 || ReconnectDelayMs < 500 || StaleAfterMs < PollIntervalMs)
        {
            return "AGV轮询、重连或过期时间配置无效。";
        }

        if (RequestIntervalMs is < 100 or > 10000 || ConnectTimeoutMs < 500 || RequestTimeoutMs < 500 || MaximumFrameBytes < 1024)
        {
            return "AGV连接超时、请求超时或最大报文长度配置无效。";
        }

        if (ProtocolVersion is not (1 or 2))
        {
            return "Agv:ProtocolVersion只支持1（RBK 3.4）或2（RBK 3.5）。";
        }

        if (!string.Equals(TaskMapPreflightMode, "Full", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(TaskMapPreflightMode, "SimulationLocal", StringComparison.OrdinalIgnoreCase))
        {
            return "Agv:TaskMapPreflightMode只支持Full或SimulationLocal。";
        }

        var enabled = Robots.Where(robot => robot.Enabled).ToArray();

        var duplicate = enabled.GroupBy(robot => robot.VehicleId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return $"AGV编号重复：{duplicate.Key}。";
        }

        foreach (var robot in enabled)
        {
            if (string.IsNullOrWhiteSpace(robot.VehicleId) ||
                string.IsNullOrWhiteSpace(robot.IpAddress) ||
                robot.Port is < 1 or > 65535 || robot.NavigationPort is < 1 or > 65535)
            {
                return $"AGV配置无效：{robot.VehicleId}。";
            }
        }

        if (AllowCommands)
        {
            if (!string.Equals(NavigationCommandMode, "SpecifiedPath", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(NavigationCommandMode, "GotargetCommissioning", StringComparison.OrdinalIgnoreCase))
            {
                return "Agv:NavigationCommandMode只支持SpecifiedPath或GotargetCommissioning。";
            }

            if (string.Equals(NavigationCommandMode, "GotargetCommissioning", StringComparison.OrdinalIgnoreCase) &&
                enabled.Length > 1)
            {
                return "3051 GotargetCommissioning模式只允许启用一台AGV。";
            }

            if (CommandConfirmationAttempts is < 1 or > 20 || CommandConfirmationDelayMs is < 50 or > 10000)
            {
                return "AGV命令确认次数或确认间隔配置无效。";
            }
        }

        return null;
    }
}

public sealed class AgvRobotOptions
{
    public bool Enabled { get; set; } = true;
    public string VehicleId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    // Legacy Port remains the status port; navigation never implicitly shares it.
    public int Port { get; set; } = 19204;
    public int NavigationPort { get; set; } = 19206;
    // Optional per-vehicle Robokit "other API" port (6005 charging relay). Null uses RobokitCharging:OtherApiPort.
    // Needed when several robots share one address, e.g. NAT port mapping or the isolated multi-vehicle simulation.
    public int? OtherApiPort { get; set; }
}

public sealed class RobokitMapBinding
{
    public string VehicleId {get;set;}="";
    public string LocalMapId {get;set;}="";
    public string LocalMapVersion {get;set;}="";
    public string RobotMap {get;set;}="";
    public string RobotMapMd5 {get;set;}="";
    public string ConfirmationNote {get;set;}="";
    public bool CoordinatesVerified {get;set;}
}
