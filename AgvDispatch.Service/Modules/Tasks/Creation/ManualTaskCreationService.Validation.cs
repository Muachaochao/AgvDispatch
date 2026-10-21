using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class ManualTaskCreationService
{
    private string? VehicleExitConfigurationError(string vehicleId,ActiveMapRuntimeSnapshot? map)
    {
        if(handshakeOptions?.Enabled!=true)return null;
        if(handshakeOptions.PlcContractVersion!=HandshakeExecutionOptions.Version)return "握手契约不是当前28阶段版本，禁止创建新任务。";
        if(map==null)return "28阶段缺少出区运行地图。";
        var exit=handshakeOptions.DumperExit;
        var profiles=exit.BodyProfiles.Where(p=>p.VehicleId==vehicleId).ToArray();
        if(profiles.Length!=1)return "28阶段出区需为所选车辆配置唯一的整车及最大载箱标定包络。";
        var validated=DumperExitEvaluator.ValidateConfiguration(map.Definition,vehicleId,profiles[0],exit.Policy);
        if(!validated.Verified)return "28阶段出区配置："+validated.Reason;
        if(!double.IsFinite(exit.MaximumRouteDeviationMeters)||exit.MaximumRouteDeviationMeters<=0||exit.MaximumRouteDeviationMeters>2)return "28阶段出区路线偏差阈值无效。";
        var dumping=map.Definition.Nodes.Where(n=>n.Enabled&&(n.Id.Equals(dispatch.DumperDumpingSite,StringComparison.OrdinalIgnoreCase)||n.AgvSiteId==dispatch.DumperDumpingSite)).ToArray();
        if(dumping.Length!=1||!dumping[0].ResourceIds.Any(id=>exit.ResourceIds.Contains(id,StringComparer.OrdinalIgnoreCase)))return "28阶段倒料节点未关联指定出区资源。";
        return null;
    }

    public IReadOnlyList<TaskParameterValidationRow> ValidateParameters(TaskParameterConfiguration configuration)
    {
        var candidateDispatch = TaskParameterRuntime.Clone(dispatch);
        var candidateCreation = TaskParameterRuntime.Clone(creation);
        TaskParameterRuntime.ApplyConfiguration(configuration, candidateDispatch, candidateCreation);
        var candidate = new ManualTaskCreationService(repository, stations, gateway, states, activeMap, gate, tasks,
            candidateCreation, candidateDispatch, steps, mapOptions, plcConfiguration, parameters, robokitMap, handshakeOptions);
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
            void DumperSignal(string name,bool write=false)
            {
                if(plcConfiguration.GetSignalConfigurationError(creation.DumperStationId,name,write) is {} error)
                    errors.Add($"PLC {creation.DumperStationId}/{name}：{error}");
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
                    if(handshakeOptions is { Enabled:true }&&handshakeOptions.PlcContractVersion==HandshakeExecutionOptions.Version){DumperSignal("AgvAtDumpingPosition");DumperSignal("DumpingCompletedMayLeave");}
                    break;
                case DispatchTaskStepType.DumpFullBox:
                    Signal("Faulted"); Signal("DumpingCompletedMayLeave"); Signal("AgvAtDumpingPosition", true);
                    break;
                case DispatchTaskStepType.PlaceEmptyBox:
                    Signal("BoxPresent"); Signal("EmptyBoxPlaced", true);
                    break;
                case DispatchTaskStepType.CompletePlcHandshake:
                    foreach (var name in new[] { "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced", "FullBoxRequestAcknowledged", "FullBoxRemoved" }) Signal(name, true);
                    Signal("BoxPresent");
                    break;
                case DispatchTaskStepType.ReturnEmptyBox when handshakeOptions is { Enabled:true }&&handshakeOptions.PlcContractVersion==HandshakeExecutionOptions.Version:
                    foreach(var name in new[]{"Faulted","AgvAtWaitingPosition","DumperReady","DumpingCompletedMayLeave"})DumperSignal(name);
                    DumperSignal("AgvAtDumpingPosition",true);
                    var exit=handshakeOptions.DumperExit;
                    if(exit.ResourceIds is not {Length:>0}||exit.ResourceIds.Any(string.IsNullOrWhiteSpace))errors.Add("28阶段实际出区必须配置DumperExit.ResourceIds，并核定倒料区域边界");
                    if(exit.BodyProfiles is not {Count:>0})errors.Add("28阶段实际出区必须配置DumperExit.BodyProfiles，包含车辆及最大约定载箱包络");
                    if(!double.IsFinite(exit.MinimumObservationIntervalSeconds)||exit.MinimumObservationIntervalSeconds<=0||
                       !double.IsFinite(exit.MaximumAgeSeconds)||exit.MaximumAgeSeconds<=0||
                       !double.IsFinite(exit.MinimumConfidence)||exit.MinimumConfidence<=0||exit.MinimumConfidence>1||
                       !double.IsFinite(exit.SafetyMarginMeters)||exit.SafetyMarginMeters<0||
                       !double.IsFinite(exit.MaximumRouteDeviationMeters)||exit.MaximumRouteDeviationMeters<=0)
                        errors.Add("28阶段出区观察间隔、新鲜度、定位置信度、安全余量和路线偏差参数无效");
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
