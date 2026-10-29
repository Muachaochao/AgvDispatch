using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed partial class ManualTaskCreationService(IManualTaskCreationRepository repository, IStationStateService stations,
    IAgvGateway gateway, IAgvStateCache states, IActiveMapRuntime activeMap, IMapExecutionGate gate,
    IDispatchOrchestrationRepository tasks, DispatchTaskCreationOptions creation, DispatchOrchestratorOptions dispatch,
    DispatchTaskStepOptions steps, MapOptions mapOptions, IPlcConfigurationInspector plcConfiguration,
    TaskParameterRuntime parameters, IRobokitTaskMapPreflight? robokitMap = null,
    HandshakeExecutionOptions? handshakeOptions = null, BusinessRunOwnership? businessRun = null, FleetTestOptions? fleetOptions=null,FleetTestOwnership? fleetOwnership=null, IChargingDispatchGuard? charging=null) : IManualTaskCreationService
{
    public async Task<ManualTaskCreationOptions> GetOptionsAsync(CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        return await GetOptionsCoreAsync(ct);
    }
    private async Task<ManualTaskCreationOptions> GetOptionsCoreAsync(CancellationToken ct)
    {
        var map = activeMap.Current;
        var reason = !gate.CanStartNewWork ? "地图切换或恢复期间暂不接受新任务。" :
            businessRun?.Returning == true ? "车辆正在返回待机或原运行待核对，暂不创建新任务。" :
            !creation.Enabled || !dispatch.Enabled || !steps.Enabled ? "任务创建、调度和步骤执行服务需全部启用。" :
            dispatch.GetConfigurationError() ?? (map is null ? "请先发布并激活运行地图。" :
                string.IsNullOrWhiteSpace(creation.DumperStationId) ? "尚未配置倒料工位。" : null);
        if(handshakeOptions?.Enabled==true&&handshakeOptions.PlcContractVersion!=HandshakeExecutionOptions.Version)
            reason="握手契约不是当前28阶段版本，禁止创建新任务。";
        reason ??= DumperWaitingBaySafety.GetError(map?.Definition, dispatch,
            handshakeOptions is { Enabled: true, PlcContractVersion: HandshakeExecutionOptions.Version });
        reason ??= dispatch.EngineeringMapError(map);
        var configured = parameters.Capture();
        var choices = dispatch.StationPickupSites.Select(pair =>
        {
            var preview = ValidateStepPreview(BuildStepPreview(pair.Key, pair.Value, map), map);
            var configurationErrors = preview.SelectMany(step => step.ValidationErrors.Select(error => $"第 {step.Sequence} 步（{step.Name}）：{error}")).ToArray();
            stations.TryGet(pair.Key, out var station);
            var block = configurationErrors.Length > 0 ? string.Join("；", configurationErrors) : station is null ? "未取得工位状态。" : !station.CanCreateTask || station.SignalQuality != SignalQuality.Good ?
                station.BlockReason ?? "PLC条件尚未满足。" : !CanRoute(map, pair.Value, dispatch.GetDumperWaitingSite()) ||
                !CanRoute(map, dispatch.GetDumperWaitingSite(), dispatch.DumperDumpingSite) ||
                !CanRoute(map, dispatch.DumperDumpingSite, pair.Value) ? "取货、倒料和返程路径未连通。" : null;
            return new ManualTaskStationChoice(pair.Key, pair.Value, block is null && reason is null, block ?? "PLC条件满足，路径已连通。")
            { PreviewSteps = preview, VehiclePoolId = configured.Pickups.SingleOrDefault(p => p.StationId == pair.Key)?.VehiclePoolId,
                AllowedVehicleIds = configured.Pickups.SingleOrDefault(p => p.StationId == pair.Key)?.AllowedVehicleIds };
        }).OrderBy(choice => choice.StationId).ToArray();
        var occupied = (await tasks.GetAssignedTasksAsync(ct)).Concat(await tasks.GetSubmittingTasksAsync(ct))
            .Where(task => task.VehicleId is not null).Select(task => task.VehicleId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicles = (await gateway.GetVehiclesAsync(ct)).Select(vehicle =>
        {
            states.TryGet(vehicle.VehicleId, out var state);
            var position = state is null ? null : AgvMapPositionAnalyzer.Analyze(state.X, state.Y, state.LocalizationConfidence,
                state.LastSuccessfulPollAt, map?.Definition, DateTimeOffset.Now, TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
                mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
            var block = vehicle.Connection != ConnectionState.Online ? "车辆离线。" :
                occupied.Contains(vehicle.VehicleId) || !dispatch.IdleOperatingStates.Contains(vehicle.OperatingState, StringComparer.OrdinalIgnoreCase) ? "车辆正在执行任务。" :
                state is null || state.Quality != SignalQuality.Good || state.Blocked is not false || state.EmergencyStopped is not false ? "车辆状态未知、阻塞或急停。" :
                position?.State != AgvMapPositionState.Normal ? position?.Message ?? "定位不可用。" :
                string.IsNullOrWhiteSpace(vehicle.CurrentStation) ? "车辆当前站点未知。" : null;
            if (charging?.AllowsNewBusiness(vehicle.VehicleId) == false) block ??= "车辆低电量或存在未闭合充电会话，暂不接新业务。";
            block ??= VehicleExitConfigurationError(vehicle.VehicleId,map);
            return new ManualTaskVehicleChoice(vehicle.VehicleId, $"{vehicle.VehicleId} · {vehicle.BatteryPercent:g}% · {vehicle.CurrentStation ?? "未知站点"}",
                vehicle.CurrentStation ?? "", block is null && reason is null, block ?? "在线空闲，定位正常。");
        }).OrderBy(vehicle => vehicle.VehicleId).ToArray();
        choices = choices.Select(choice => choice with
        { PickupReachableVehicleIds = vehicles.Where(vehicle => dispatch.AllowsVehicle(choice.StationId, vehicle.VehicleId) && CanRoute(map, vehicle.CurrentSite, choice.PickupSite, true, PickupExcludedNodes(map))).Select(vehicle => vehicle.VehicleId).ToArray() }).ToArray();
        reason ??= !choices.Any(station => station.Available) ? "暂无满足 PLC 条件和路径要求的工位。" :
            !vehicles.Any(vehicle => vehicle.Available) ? "暂无可派车辆：" +
                string.Join("；", vehicles.Select(vehicle => vehicle.VehicleId + "：" + vehicle.Explanation)) : null;
        return new(map?.MapId, map?.VersionId, reason is null, reason ?? "使用当前运行地图创建取满箱、倒料、返空箱九步任务。",
            creation.DumperStationId, dispatch.GetDumperWaitingSite() ?? "", dispatch.DumperDumpingSite, choices, vehicles)
        { ParameterRevision = parameters.Revision, RequiresExplicitVehicle = robokitMap is not null, MapRuntimeId=map?.RuntimeId??"", Standbys = configured.Standbys, WaitingSites = dispatch.GetDumperWaitingSites() };
    }

    private IReadOnlySet<string> PickupExcludedNodes(ActiveMapRuntimeSnapshot? map) => map is null ? new HashSet<string>() :
        dispatch.GetDumperWaitingSites().Append(dispatch.DumperDumpingSite).Concat(dispatch.StandbySites).Where(site => !string.IsNullOrWhiteSpace(site))
            .Select(site => MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, site!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool CanRoute(ActiveMapRuntimeSnapshot? map, string? from, string? to, bool requireTransport = false, IReadOnlySet<string>? forbiddenNodes = null)
    {
        if (map is null || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
        string? Resolve(string site)
        {
            var found = map.Definition.Nodes.Where(node => node.Enabled &&
                (node.Id.Equals(site, StringComparison.OrdinalIgnoreCase) || string.Equals(node.AgvSiteId, site, StringComparison.OrdinalIgnoreCase))).ToArray();
            return found.Length == 1 ? found[0].Id : null;
        }
        var source = Resolve(from); var destination = Resolve(to);
        return source is not null && destination is not null &&
            (requireTransport ? map.Graph.FindShortestTransportRoute(source, destination, forbiddenIntermediateNodes: forbiddenNodes) : map.Graph.FindShortestRoute(source, destination)) is { Nodes.Count: >= 2 };
    }

    public async Task<ManualTaskCreationResult> CreateAsync(TrafficUserContext user, ManualTaskCreationRequest request, CancellationToken ct)
    {
        using var chargingLease = charging is null ? null : await charging.EnterBusinessAsync(ct);
        if(fleetOptions?.Enabled==true||fleetOwnership?.Active==true)throw new InvalidOperationException("双车测试模式或原会话尚未闭合，不能另建手工任务。");
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || string.IsNullOrWhiteSpace(request.StationId) ||
            request.StationId.Length > 32 || request.RequestedVehicleId?.Length > 32 || request.Priority is < 0 or > 100 ||
            string.IsNullOrWhiteSpace(request.MapId) || string.IsNullOrWhiteSpace(request.MapVersionId) ||
            request.MapId.Length > 64 || request.MapVersionId.Length > 64)
            return new(false, false, null, "请求编号、工位、车辆、运行地图或优先级无效；优先级范围 0–100。");
        request = request with { RequestedVehicleId = string.IsNullOrWhiteSpace(request.RequestedVehicleId) ? null : request.RequestedVehicleId };
        var receipt = await repository.FindRequestAsync(request.RequestId, ct);
        if (receipt is not null)
            return receipt.StationId == request.StationId && receipt.RequestedVehicleId == request.RequestedVehicleId && receipt.Priority == request.Priority &&
                   receipt.MapId == request.MapId && receipt.MapVersionId == request.MapVersionId && receipt.ParameterRevision == request.ParameterRevision
                ? new(true, false, receipt.TaskId, "该请求已经创建任务，未重复提交。")
                : new(false, false, null, "请求编号已用于不同参数，请核对任务。");
        using var lease = await gate.EnterAsync(MapExecutionOperation.NewWork, ct);
        if (lease is null) return new(false, false, null, "当前不允许新任务，请等待地图切换或恢复完成。");
        if (businessRun?.IsRetired(request.RequestId) == true)
            return new(false, false, null, "原申请已核对注销，禁止重放或迟到创建；请查询原运行。");
        if (businessRun?.Active == true && businessRun.RequestId != request.RequestId)
            return new(false, false, null, "持续运行正在接续呼叫，请先停止新增并收尾，不接受额外手动任务。");
        if (request.ParameterRevision != parameters.Revision)
            return new(false, false, null, "任务参数已变化，请刷新创建选项后提交。");
        var options = await GetOptionsCoreAsync(ct);
        if (options.MapId != request.MapId || options.MapVersionId != request.MapVersionId)
            return new(false, false, null, "运行地图已变化，请刷新创建选项。");
        var station = options.Stations.FirstOrDefault(station => station.StationId == request.StationId);
        if (station is { ConfigurationValid: false }) return new(false, false, null, station.Explanation);
        if (!options.Available) return new(false, false, null, options.Explanation);
        if (station is not { Available: true }) return new(false, false, null, station?.Explanation ?? "工位不在当前调度配置中。");
        if (request.RequestedVehicleId is { } requested && !dispatch.AllowsVehicle(request.StationId, requested))
            return new(false, false, null, $"工位 {request.StationId} 的车辆池不包含 {requested}，未创建任务。");
        var vehicles = options.Vehicles.Where(vehicle => vehicle.Available && dispatch.AllowsVehicle(request.StationId, vehicle.VehicleId) && CanRoute(activeMap.Current, vehicle.CurrentSite, station.PickupSite, true, PickupExcludedNodes(activeMap.Current)));
        if (request.RequestedVehicleId is not null) vehicles = vehicles.Where(vehicle => vehicle.VehicleId == request.RequestedVehicleId);
        if (!vehicles.Any()) return new(false, false, null, options.Vehicles.FirstOrDefault(v=>v.VehicleId==request.RequestedVehicleId&& !v.Available)?.Explanation??"第 1 步（前往取货点）：所选车辆当前不可用或无可执行路径；车辆已在取货点时必须配置避开等待/倒料节点的真实已启用进出闭合路线，不能用同点空路径代替取箱动作。");
        if (robokitMap is not null)
        {
            if(request.RequestedVehicleId is null)return new(false,false,null,"车端地图核验需要明确选择车辆，不能以自动派车跳过核验。");
            var vehicle=vehicles.Single();
            var mapBefore=activeMap.Current;
            var checkedMap=await robokitMap.CheckAsync(vehicle.VehicleId,vehicle.CurrentSite,station.PickupSite,
                options.WaitingSite,options.DumpingSite,request.MapId,request.MapVersionId,ct);
            if(!checkedMap.Accepted)return new(false,false,null,checkedMap.Message);
            var latest=await GetOptionsCoreAsync(ct);
            var latestVehicle=latest.Vehicles.FirstOrDefault(v=>v.VehicleId==vehicle.VehicleId);
            var latestStation=latest.Stations.FirstOrDefault(s=>s.StationId==request.StationId);
            if(!ReferenceEquals(mapBefore,activeMap.Current) || parameters.Revision!=request.ParameterRevision ||
                !latest.Available || latestVehicle is not {Available:true} || latestVehicle.CurrentSite!=vehicle.CurrentSite || latestStation is not {Available:true})
                return new(false,false,null,"车端核验期间运行地图、参数、工位或车辆状态变化；未创建任务，请刷新。");
        }
        var result = await repository.TryCreateManualAsync(request, creation.DumperStationId, user.WindowsIdentity, ct);
        if (!result.Created)
        {
            var replay = await repository.FindRequestAsync(request.RequestId, ct);
            return new(replay is not null, false, result.Task.TaskId, replay is not null ? "该请求已创建，未重复提交。" : "工位已有未完成任务，请查看现有任务。");
        }
        return new(true, true, result.Task.TaskId, "九步任务已创建，等待后台 PLC 应答后进入派车；车辆和路径将在执行时再次检查。");
    }
}
