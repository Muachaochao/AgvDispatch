using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class ManualTaskCreationService
{
    public IReadOnlyList<TaskParameterValidationRow> ValidateParameters(TaskParameterConfiguration configuration)
    {
        var candidateDispatch = TaskParameterRuntime.Clone(dispatch);
        var candidateCreation = TaskParameterRuntime.Clone(creation);
        TaskParameterRuntime.ApplyConfiguration(configuration, candidateDispatch, candidateCreation);
        var candidate = new ManualTaskCreationService(repository, stations, gateway, states, activeMap, gate, tasks,
            candidateCreation, candidateDispatch, steps, mapOptions, plcConfiguration, parameters);
        var map = activeMap.Current;
        return configuration.Pickups.SelectMany(pickup => candidate.ValidateStepPreview(candidate.BuildStepPreview(pickup.StationId, pickup.PickupSite, map), map)
            .Select(step => new TaskParameterValidationRow(pickup.StationId, step))).ToArray();
    }
    private IReadOnlyList<ManualTaskStepPreview> ValidateStepPreview(IReadOnlyList<ManualTaskStepPreview> preview, ActiveMapRuntimeSnapshot? map)
    {
        return preview.Select(step =>
        {
            var errors = new List<string>();
            void Signal(string name, bool write = false)
            {
                if (plcConfiguration.GetSignalConfigurationError(step.PlcStationId, name, write) is { } error)
                    errors.Add($"PLC {step.PlcStationId}/{name}：{error}");
            }
            var transport = step.StepType is DispatchTaskStepType.GoToPickup or DispatchTaskStepType.DeliverFullBoxToDumper or
                DispatchTaskStepType.EnterDumpingPosition or DispatchTaskStepType.ReturnEmptyBox;
            if (transport)
            {
                var action = dispatch.GetAction(step.StepType);
                if (action is null) errors.Add($"缺少 Dispatching:StepActions:{step.StepType} 动作；仅导航也需明确配置 Wait");
                else
                {
                    if (!new[] { "Wait", "JackLoad", "JackUnload", "JackHeight" }.Contains(action.Operation, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"不支持车辆动作 {action.Operation}");
                    if (action.JackHeight is { } height && (!double.IsFinite(height) || height < 0) ||
                        action.Operation.Equals("JackHeight", StringComparison.OrdinalIgnoreCase) && action.JackHeight is null)
                        errors.Add("JackHeight 必须配置为有限且非负的数值");
                }
                if (step.Sequence != 1 && RouteConfigurationError(map, step.SourceSite, step.DestinationSite) is { } routeError)
                    errors.Add(routeError);
            }
            switch (step.StepType)
            {
                case DispatchTaskStepType.GoToPickup:
                    if (ResolveSiteError(map, step.DestinationSite) is { } pickupError) errors.Add(pickupError);
                    foreach (var name in new[] { "FullBoxRequested", "BoxPresent", "Faulted", "CommunicationOnline" }) Signal(name);
                    Signal("FullBoxRequestAcknowledged", true);
                    break;
                case DispatchTaskStepType.PickupFullBox:
                    Signal("BoxPresent"); Signal("FullBoxRemoved", true); Signal("EmptyBoxTaskAcknowledged", true); Signal("FullBoxRequestAcknowledged", true);
                    break;
                case DispatchTaskStepType.WaitForDumperReady:
                    Signal("Faulted"); Signal("DumperReady"); Signal("AgvAtWaitingPosition", true);
                    break;
                case DispatchTaskStepType.DumpFullBox:
                    Signal("Faulted"); Signal("DumpingCompletedMayLeave"); Signal("AgvAtDumpingPosition", true);
                    break;
                case DispatchTaskStepType.PlaceEmptyBox:
                    Signal("BoxPresent"); Signal("EmptyBoxPlaced", true);
                    break;
                case DispatchTaskStepType.CompletePlcHandshake:
                    foreach (var name in new[] { "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced", "FullBoxRequestAcknowledged", "FullBoxRemoved" }) Signal(name, true);
                    break;
            }
            return step with { ValidationErrors = errors };
        }).ToArray();
    }

    private static string? ResolveSiteError(ActiveMapRuntimeSnapshot? map, string? site)
    {
        if (map is null) return "尚未激活运行地图";
        if (string.IsNullOrWhiteSpace(site)) return "AGV 站点未配置";
        var matches = map.Definition.Nodes.Count(node => node.Enabled &&
            (string.Equals(node.Id, site, StringComparison.OrdinalIgnoreCase) || string.Equals(node.AgvSiteId, site, StringComparison.OrdinalIgnoreCase)));
        return matches == 1 ? null : $"站点 {site} 的启用节点映射数量为 {matches}，必须唯一";
    }

    private static string? RouteConfigurationError(ActiveMapRuntimeSnapshot? map, string from, string to) =>
        ResolveSiteError(map, from) ?? ResolveSiteError(map, to) ??
        (CanRoute(map, from, to) ? null : $"路线 {from} → {to} 不可达或路径节点不足（至少 2 个节点）");
}
