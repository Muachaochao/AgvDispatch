using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class DispatchOrchestratorOptions
{
    public const string SectionName = "Dispatching";

    public bool Enabled { get; set; }
    public bool RequireGlobalMatching { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    public int MaxConcurrentTasks { get; set; } = 1;
    public string DumperSite { get; set; } = string.Empty;
    public string DumperWaitingSite { get; set; } = string.Empty;
    public string[] DumperWaitingSites { get; set; } = [];
    public string[] StandbySites { get; set; } = [];
    public string DumperDumpingSite { get; set; } = string.Empty;
    public double DumperTransitClearanceMeters { get; set; }
    public bool SimulationB07VerticalChargingInlet { get; set; }
    public bool SimulationDeferSharedChargingExitReservation { get; set; }
    public string[] IdleOperatingStates { get; set; } = ["Idle", "空闲"];
    public bool AllowUnconfiguredDirectRoutes { get; set; }
    public Dictionary<string, string> StationPickupSites { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> StationVehiclePools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? EngineeringMapId { get; set; }
    public string? EngineeringMapVersion { get; set; }
    public string? EngineeringMapHash { get; set; }
    public bool AllowsVehicle(string stationId, string vehicleId) =>
        !StationVehiclePools.TryGetValue(stationId, out var ids) || ids.Contains(vehicleId, StringComparer.OrdinalIgnoreCase);
    public string? EngineeringMapError(AgvDispatch.Application.Abstractions.ActiveMapRuntimeSnapshot? map) =>
        EngineeringMapId is null ? null : map is null || map.MapId != EngineeringMapId || map.VersionId != EngineeringMapVersion ||
        map.DefinitionHash != EngineeringMapHash
        ? "工程任务参数与当前运行地图版本或内容不一致，请在工程执行接入页重新核对并应用；禁止沿用旧图参数派车。" : null;
    public Dictionary<string, string[]> Routes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AgvStepActionOptions> StepActions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string? GetPickupSite(string stationId) =>
        StationPickupSites.TryGetValue(stationId, out var site) &&
        !string.IsNullOrWhiteSpace(site)
            ? site
            : null;

    public string? GetDumperWaitingSite() =>
        !string.IsNullOrWhiteSpace(DumperWaitingSite)
            ? DumperWaitingSite
            : !string.IsNullOrWhiteSpace(DumperSite) ? DumperSite : null;

    public IReadOnlyList<string> GetDumperWaitingSites() => DumperWaitingSites.Length > 0
        ? DumperWaitingSites : GetDumperWaitingSite() is { } site ? [site] : [];

    public IReadOnlyList<string>? GetRoute(string source, string destination)
    {
        var key = $"{source}->{destination}";
        if (Routes.TryGetValue(key, out var route) &&
            route.Length >= 2 &&
            string.Equals(route[0], source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(route[^1], destination, StringComparison.OrdinalIgnoreCase))
        {
            return route;
        }

        return AllowUnconfiguredDirectRoutes ? [source, destination] : null;
    }

    public AgvTransportAction? GetAction(DispatchTaskStepType stepType)
    {
        if (!StepActions.TryGetValue(stepType.ToString(), out var configured) ||
            string.IsNullOrWhiteSpace(configured.Operation))
        {
            return null;
        }

        return new AgvTransportAction(
            configured.Operation,
            configured.JackHeight,
            configured.Recognize,
            configured.UsePgv,
            configured.UseDownPgv);
    }

    /// <summary>
    /// Startup checks that need the configured fleet as well as the dispatch options. Run once before the host
    /// is built, so a wrong site, pool or concurrency setting stops the service instead of failing in the first cycle.
    /// </summary>
    public string? GetStartupError(IReadOnlyCollection<string> configuredVehicleIds)
    {
        if (GetConfigurationError() is { } error) return error;
        if (!Enabled) return null;
        if (configuredVehicleIds.Count == 0)
            return "启用调度前必须至少配置一台启用的AGV车辆。";
        if (MaxConcurrentTasks > configuredVehicleIds.Count)
            return $"Dispatching:MaxConcurrentTasks({MaxConcurrentTasks})不能超过已配置的AGV车辆数({configuredVehicleIds.Count})。";
        if (StationPickupSites.Count == 0)
            return "启用调度前必须配置Dispatching:StationPickupSites，至少一个工位的取货站点。";
        if (StationPickupSites.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            return "Dispatching:StationPickupSites中存在空的工位或取货站点。";
        var dumperSites = new HashSet<string>(GetDumperWaitingSites().Append(DumperDumpingSite), StringComparer.OrdinalIgnoreCase);
        if (StationPickupSites.Values.FirstOrDefault(site => dumperSites.Contains(site)) is { } conflict)
            return $"取货站点{conflict}与倒料候车或倒料站点冲突。";
        var vehicles = new HashSet<string>(configuredVehicleIds, StringComparer.OrdinalIgnoreCase);
        var unknown = StationVehiclePools.Values.SelectMany(ids => ids)
            .FirstOrDefault(id => !vehicles.Contains(id));
        if (unknown is not null)
            return $"Dispatching:StationVehiclePools引用了未配置的AGV车辆：{unknown}。";
        return null;
    }

    public string? GetConfigurationError()
    {
        if (!Enabled)
        {
            return null;
        }

        if (PollIntervalMs < 500)
        {
            return "Dispatching:PollIntervalMs不能小于500ms。";
        }

        if (MaxConcurrentTasks < 1)
        {
            return "Dispatching:MaxConcurrentTasks必须大于0。";
        }

        if (GetDumperWaitingSite() is null)
        {
            return "启用调度前必须配置Dispatching:DumperWaitingSite。";
        }

        if (string.IsNullOrWhiteSpace(DumperDumpingSite))
        {
            return "启用调度前必须配置Dispatching:DumperDumpingSite。";
        }

        if (IdleOperatingStates.Length == 0)
        {
            return "Dispatching:IdleOperatingStates不能为空。";
        }

        var allowedOperations = new HashSet<string>(
            ["Wait", "JackLoad", "JackUnload", "JackHeight"],
            StringComparer.OrdinalIgnoreCase);
        var invalidAction = StepActions.FirstOrDefault(pair =>
            !string.IsNullOrWhiteSpace(pair.Value.Operation) &&
            !allowedOperations.Contains(pair.Value.Operation));
        if (!string.IsNullOrEmpty(invalidAction.Key))
        {
            return $"Dispatching:StepActions中的动作无效：{invalidAction.Key}/{invalidAction.Value.Operation}。";
        }

        return null;
    }
}

public sealed class AgvStepActionOptions
{
    public string Operation { get; set; } = "Wait";
    public double? JackHeight { get; set; }
    public bool? Recognize { get; set; }
    public bool? UsePgv { get; set; }
    public bool? UseDownPgv { get; set; }
}
