using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class MapSwitchService(ISqlSugarClient database, IMapExecutionGate gate,
    IActiveMapRuntime runtime, IAgvStateCache vehicles, IPlcSignalCache signals,
    MapSwitchSafetySettings settings,IMapTransferRecoveryBarrier? mapTransfers=null, IBusinessRunInterlock? businessRun=null) : IMapSwitchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var control = await gate.EnterControlAsync(cancellationToken);
        // A restart never silently resumes an unfinished release, even before the DB pointer changes.
        gate.SetMode(await OpenAsync(cancellationToken) is null && mapTransfers?.IsOpen!=false ? MapExecutionMode.Running : MapExecutionMode.Held);
    }

    public async Task<MapSwitchStatus> GetAsync(TrafficUserContext user, CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        using var control = await gate.EnterControlAsync(cancellationToken);
        var current = await OpenAsync(cancellationToken);
        var history = await database.Queryable<MapSwitchEntity>()
            .OrderBy(row => row.StartedAt, OrderByType.Desc).Take(30).ToListAsync(cancellationToken);
        var definition = current is null ? runtime.Current?.Definition :
            await DefinitionAsync(current.VersionId, cancellationToken);
        var blockers = definition is null ? new[] { "没有可校验的目标地图。" } :
            await GetBlockersAsync(definition, cancellationToken);
        return new(gate.Mode, current is null ? null : Snapshot(current), blockers, history.Select(Snapshot).ToArray());
    }

    public async Task<MapSwitchResult> BeginAsync(TrafficUserContext user, BeginMapSwitchRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        using var control = await gate.EnterControlAsync(cancellationToken);
        if (gate.Mode == MapExecutionMode.Starting) return new(false, "运行地图尚未初始化。");
        if (businessRun?.Active == true) return new(false, "请先停止业务运行并完成待机收尾；原运行待核对时不能切图。");
        var existing = await OpenAsync(cancellationToken);
        if (existing is not null) return new(false, $"已有切换{existing.ReleaseId}，请先完成或取消。");
        var version = await database.Queryable<MapVersionEntity>().Where(row =>
            row.MapId == request.MapId && row.VersionId == request.VersionId &&
            row.Status == (int)MapVersionStatus.Published).FirstAsync(cancellationToken);
        var target = await database.Queryable<MapProjectEntity>().Where(row => row.MapId == request.MapId)
            .FirstAsync(cancellationToken);
        if (version is null || target is null || target.IsArchived || target.PublishedVersionId != request.VersionId)
            return new(false, "目标不是当前已发布版本。");
        var candidate = JsonSerializer.Deserialize<MapDefinition>(version.DefinitionJson, JsonOptions);
        if (candidate is null || candidate.CoordinateSystem == "SmapWorldTranslated" ||
            !candidate.Nodes.Any(n => n.Enabled && n.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint) ||
            !candidate.Edges.Any(e => e.Enabled && candidate.Nodes.Any(n => n.Enabled && n.Id == e.From) && candidate.Nodes.Any(n => n.Enabled && n.Id == e.To)))
            return new(false, "目标地图尚未配置为运行图：请在地图编辑中使用‘准备运行地图’，还原车端坐标、确认站点用途并启用道路后，保存并重新发布。尚未开始切换。");
        var previous = await database.Queryable<MapProjectEntity>().Where(row => row.IsActive)
            .FirstAsync(cancellationToken);
        if (previous?.MapId == request.MapId && previous.ActiveVersionId == request.VersionId)
            return new(false, "该版本已经是运行地图。");
        var now = DateTime.Now;
        // Close first; ambiguous persistence failure must not reopen command sending.
        gate.SetMode(MapExecutionMode.Held);
        await database.Insertable(new MapSwitchEntity
        {
            ReleaseId = $"MAPR-{Guid.NewGuid():N}", MapId = request.MapId, VersionId = request.VersionId,
            PreviousMapId = previous?.MapId, PreviousVersionId = previous?.ActiveVersionId,
            Phase = (int)MapSwitchPhase.Draining, OperatorIdentity = user.WindowsIdentity,
            StartedAt = now, UpdatedAt = now
        }).ExecuteCommandAsync(cancellationToken);
        gate.SetMode(MapExecutionMode.Draining);
        return new(true, "已停止新增派单；原任务及其命令、PLC步骤继续收尾。现场变化前请确保旧路线仍然安全。");
    }

    public async Task<MapSwitchResult> ContinueDrainAsync(TrafficUserContext user, FinishMapSwitchRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        using var control = await gate.EnterControlAsync(cancellationToken);
        if(mapTransfers?.IsOpen==false)return new(false,"地图传输结果未确定，禁止继续收尾或放行。");
        var current = await OpenAsync(cancellationToken);
        if (gate.Mode != MapExecutionMode.Held || current is null || current.ReleaseId != request.ReleaseId ||
            current.Phase != (int)MapSwitchPhase.Draining)
            return new(false, "只有中断且尚未提交的切换可以恢复原任务收尾。");
        if (!request.SiteSafetyConfirmed || !request.VehicleMapCompatibilityConfirmed || !request.OriginalWorkflowConfirmed ||
            string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 1500)
            return new(false, "请核对原地图、现场安全，以及所有未完成任务、命令和PLC流程节点，并记录恢复依据。");
        var active = await database.Queryable<MapProjectEntity>().Where(row => row.IsActive).FirstAsync(cancellationToken);
        if (active?.MapId != current.PreviousMapId || active?.ActiveVersionId != current.PreviousVersionId ||
            runtime.Current?.MapId != current.PreviousMapId || runtime.Current?.VersionId != current.PreviousVersionId ||
            runtime.Current is null)
            return new(false, "原运行地图尚未一致加载，禁止恢复收尾。");
        var blockers = GetPhysicalBlockers(runtime.Current.Definition, true);
        if (blockers.Count > 0) return new(false, string.Join("；", blockers));
        var updated = await database.Updateable<MapSwitchEntity>().SetColumns(row => new MapSwitchEntity
        {
            Evidence = row.Evidence + "\n恢复原任务收尾：" + user.WindowsIdentity + ": " + request.Evidence.Trim(),
            UpdatedAt = DateTime.Now
        }).Where(row => row.ReleaseId == current.ReleaseId && row.Phase == (int)MapSwitchPhase.Draining)
            .ExecuteCommandAsync(cancellationToken);
        if (updated != 1) return new(false, "切换记录已变化，保持暂停。");
        gate.SetMode(MapExecutionMode.Draining);
        return new(true, "已按现场核对结果恢复原任务收尾；仍禁止新增派单。");
    }

    public Task<MapSwitchResult> ResumeAsync(TrafficUserContext user, FinishMapSwitchRequest request,
        CancellationToken cancellationToken) => FinishAsync(user, request, false, cancellationToken);
    public Task<MapSwitchResult> CancelAsync(TrafficUserContext user, FinishMapSwitchRequest request,
        CancellationToken cancellationToken) => FinishAsync(user, request, true, cancellationToken);

    private async Task<MapSwitchResult> FinishAsync(TrafficUserContext user, FinishMapSwitchRequest request,
        bool cancel, CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        using var control = await gate.EnterControlAsync(cancellationToken);
        var current = await OpenAsync(cancellationToken);
        if (current is null || current.ReleaseId != request.ReleaseId) return new(false, "切换记录已变化，请刷新。");
        if(mapTransfers?.IsOpen==false)return new(false,"地图传输结果未确定，不能取消屏障或放行。");
        if (cancel && current.Phase != (int)MapSwitchPhase.Draining)
            return new(false, "地图已经提交，不能用取消恢复旧指针；需重新发布并完成安全切换。");
        if (!cancel && current.Phase != (int)MapSwitchPhase.AwaitingResume)
            return new(false, "请先完成地图激活；中断的收尾流程可在确认现场后取消并重新开始。");
        if (!request.SiteSafetyConfirmed || !request.VehicleMapCompatibilityConfirmed ||
            string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 1500)
            return new(false, "请确认现场安全和车辆地图兼容，并填写1至1500字的核对依据。");
        var expectedMap = cancel ? current.PreviousMapId : current.MapId;
        var expectedVersion = cancel ? current.PreviousVersionId : current.VersionId;
        var active = await database.Queryable<MapProjectEntity>().Where(row => row.IsActive).FirstAsync(cancellationToken);
        if (active?.MapId != expectedMap || active?.ActiveVersionId != expectedVersion ||
            runtime.Current?.MapId != expectedMap || runtime.Current?.VersionId != expectedVersion)
            return new(false, "数据库与运行地图不一致，禁止放行；请重启服务后重新核对。");
        var definition = runtime.Current?.Definition ?? await DefinitionAsync(current.VersionId, cancellationToken);
        var cancelledPreview = cancel && (await DefinitionAsync(current.VersionId, cancellationToken))?.CoordinateSystem == "SmapWorldTranslated";
        // During same-process draining, cancel can preserve ongoing work. After interruption require full quiescence.
        if (!cancel || cancelledPreview || gate.Mode != MapExecutionMode.Draining)
        {
            if (definition is null) return new(false, "地图定义不存在。");
            // Cancelling a never-committed first activation returns to no map.
            // Its unused preview geometry must not trap that cancellation forever.
            // Keep all task/lease/command, telemetry, stationary and PLC checks.
            var bootstrapCancel=cancel && current.PreviousMapId is null && current.PreviousVersionId is null &&
                active is null && runtime.Current is null;
            // An unused preview is never an executable coordinate reference. Close it only after
            // full quiescence checks; retain Held so cancelling cannot resume the old-map workflow.
            var blockers = await GetBlockersCoreAsync(definition, !bootstrapCancel && !cancelledPreview, cancellationToken);
            if (blockers.Count > 0) return new(false, string.Join("；", blockers));
            var committedAt = new DateTimeOffset(current.UpdatedAt);
            if (!cancel && (vehicles.GetAll().Any(vehicle => vehicle.Quality != SignalQuality.Disabled &&
                    vehicle.LastSuccessfulPollAt < committedAt) ||
                (settings.CheckPlcSignals && signals.GetAll().Any(signal => signal.Enabled &&
                    IsPlcInput(signal) && signal.UpdatedAt < committedAt))))
                return new(false, "激活后尚未取得新的车辆或PLC状态，请刷新后再放行。");
        }
        var changed = await database.Updateable<MapSwitchEntity>()
            .SetColumns(row => new MapSwitchEntity
            {
                Phase = cancel ? (int)MapSwitchPhase.Cancelled : (int)MapSwitchPhase.Completed,
                Evidence = row.Evidence + "\n" + user.WindowsIdentity + ": " + request.Evidence.Trim(),
                UpdatedAt = DateTime.Now
            }).Where(row => row.ReleaseId == current.ReleaseId && row.Phase == current.Phase)
            .ExecuteCommandAsync(cancellationToken);
        if (changed != 1) return new(false, "切换记录已变化，保持停止派单。");
        gate.SetMode(cancelledPreview ? MapExecutionMode.Held : MapExecutionMode.Running);
        return new(true, cancelledPreview ? "已关闭未提交的预览图切换；原运行图保留且执行仍暂停。请配置并发布运行图后重新安全切换。" : cancel ? "已取消切换，继续使用原地图。" : "现场与车辆兼容性已确认，恢复派单。");
    }

    public async Task<IReadOnlyList<string>> GetBlockersAsync(MapDefinition definition, CancellationToken cancellationToken)
        => await GetBlockersCoreAsync(definition,true,cancellationToken);

    private async Task<IReadOnlyList<string>> GetBlockersCoreAsync(MapDefinition definition,bool checkTargetMap,CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        if(mapTransfers?.IsOpen==false)blockers.Add("存在未处置的地图传输不确定记录，不能解除执行屏障。");
        var tasks = await database.Queryable<DispatchTaskEntity>().Where(row =>
            row.Stage != (int)DispatchTaskStage.Completed && row.Stage != (int)DispatchTaskStage.Faulted &&
            row.Stage != (int)DispatchTaskStage.Cancelled &&
            ((row.Stage != (int)DispatchTaskStage.Created && row.Stage != (int)DispatchTaskStage.WaitingForVehicle) ||
             row.VehicleId != null)).CountAsync(cancellationToken);
        if (tasks > 0) blockers.Add($"执行或已分配任务{tasks}条");
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().CountAsync(cancellationToken);
        if (leases > 0) blockers.Add($"物理资源占用{leases}条（不因超时清除）");
        var commands = await database.Queryable<AgvCommandOutboxEntity>().Where(row =>
            row.Status == (int)AgvCommandStatus.Pending || row.Status == (int)AgvCommandStatus.Sending ||
            row.Status == (int)AgvCommandStatus.RetryScheduled || row.Status == (int)AgvCommandStatus.Failed)
            .CountAsync(cancellationToken);
        if (commands > 0) blockers.Add($"待发送或结果未闭环命令{commands}条");
        var steps = await database.Queryable<DispatchTaskStepEntity>().Where(row =>
            row.Status == (int)DispatchTaskStepStatus.CommandPending || row.Status == (int)DispatchTaskStepStatus.Sending ||
            row.Status == (int)DispatchTaskStepStatus.Executing || row.Status == (int)DispatchTaskStepStatus.RetryScheduled ||
            row.Status == (int)DispatchTaskStepStatus.ManualIntervention || row.Status == (int)DispatchTaskStepStatus.Failed)
            .CountAsync(cancellationToken);
        if (steps > 0) blockers.Add($"执行或异常未确认步骤{steps}条");
        blockers.AddRange(GetPhysicalBlockers(definition, false,checkTargetMap));
        return blockers;
    }

    private IReadOnlyList<string> GetPhysicalBlockers(MapDefinition definition, bool allowExecution,bool checkTargetMap=true)
    {
        var blockers = new List<string>();
        var now = DateTimeOffset.Now;
        foreach (var id in settings.RequiredVehicleIds.Concat(vehicles.GetAll()
                     .Where(vehicle => vehicle.Quality != SignalQuality.Disabled).Select(vehicle => vehicle.VehicleId))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!vehicles.TryGet(id, out var vehicle)) { blockers.Add($"{id}状态缺失"); continue; }
            if (vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good ||
                vehicle.LastSuccessfulPollAt is null || now - vehicle.LastSuccessfulPollAt > settings.StateMaxAge ||
                vehicle.LastSuccessfulPollAt > now.AddSeconds(1))
            { blockers.Add($"{id}离线或状态过期"); continue; }
            if (vehicle.EmergencyStopped != false || vehicle.Blocked != false || vehicle.Alarms.Any(alarm =>
                alarm.Severity >= AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error)) blockers.Add($"{id}急停、阻挡或故障状态未正常确认");
            if (!allowExecution && (vehicle.Tasks.Any(task => task.Status is not (4 or 5 or 6 or 7)) ||
                vehicle.NavigationState is not ("0" or "4" or "5" or "6"))) blockers.Add($"{id}导航未空闲");
            if (!allowExecution && (!Stopped(vehicle.VelocityX) || !Stopped(vehicle.VelocityY) || !Stopped(vehicle.AngularVelocity)))
                blockers.Add($"{id}静止状态未确认");
            if (vehicle.LocalizationConfidence is null) blockers.Add($"{id}定位置信度缺失");
            if(!checkTargetMap)
            {
                if(vehicle.LocalizationConfidence is not { } confidence || !double.IsFinite(confidence) || confidence<settings.MinimumLocalizationConfidence)
                    blockers.Add($"{id}定位置信度不足");
                continue;
            }
            var position = AgvMapPositionAnalyzer.Analyze(vehicle.X, vehicle.Y, vehicle.LocalizationConfidence,
                vehicle.LastSuccessfulPollAt, definition, now, settings.StateMaxAge,
                settings.MinimumLocalizationConfidence, settings.MaximumRouteDeviationMeters);
            if (position.State != AgvMapPositionState.Normal) blockers.Add($"{id}目标地图定位检查：{position.Message}");
            var station = definition.Nodes.FirstOrDefault(node => node.Enabled &&
                node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint &&
                string.Equals(node.AgvSiteId, vehicle.CurrentStation, StringComparison.OrdinalIgnoreCase));
            if (!allowExecution && (station is null || vehicle.X is null || vehicle.Y is null ||
                Math.Sqrt(Math.Pow(station.X - vehicle.X.Value, 2) + Math.Pow(station.Y - vehicle.Y.Value, 2)) >
                settings.MaximumStationDeviationMeters)) blockers.Add($"{id}未在目标地图的有效站点安全停靠");
        }
        if (settings.CheckPlcSignals || signals.GetAll().Any(signal =>
                signal.Quality is SignalQuality.Good or SignalQuality.Stale or SignalQuality.Bad))
        {
            var enabled = signals.GetAll().Where(signal => signal.Enabled && IsPlcInput(signal)).ToArray();
            if (enabled.Length == 0) blockers.Add("PLC输入状态缺失");
            var invalid = enabled.Where(signal => signal.Quality != SignalQuality.Good || signal.Value is null ||
                signal.UpdatedAt is null || now - signal.UpdatedAt > settings.StateMaxAge ||
                signal.UpdatedAt > now.AddSeconds(1)).ToArray();
            if (invalid.Length > 0) blockers.Add($"PLC输入失效{invalid.Length}个：" +
                string.Join(',', invalid.Take(5).Select(signal => $"{signal.StationId}/{signal.VariableName}")));
            var faults = enabled.Where(signal => signal.VariableName == "Faulted" && signal.Value == true).ToArray();
            if (faults.Length > 0) blockers.Add($"PLC工位故障{faults.Length}个");
        }
        return blockers;
    }

    // Current CSV point tables use PLC->Host; older catalogs use PlcToHost.
    // Both must participate in freshness and fault checks, including post-activation checks.
    private static bool IsPlcInput(PlcSignalSnapshot signal) =>
        string.Equals(signal.Direction?.Trim(), "PlcToHost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(signal.Direction?.Trim(), "PLC->Host", StringComparison.OrdinalIgnoreCase);

    private bool Stopped(double? value) => value is { } number && double.IsFinite(number) &&
        Math.Abs(number) <= settings.StoppedVelocityTolerance;
    private async Task<MapDefinition?> DefinitionAsync(string versionId, CancellationToken cancellationToken)
    {
        var version = await database.Queryable<MapVersionEntity>().Where(row => row.VersionId == versionId)
            .FirstAsync(cancellationToken);
        return version is null ? null : JsonSerializer.Deserialize<MapDefinition>(version.DefinitionJson, JsonOptions);
    }
    private Task<MapSwitchEntity> OpenAsync(CancellationToken cancellationToken) =>
        database.Queryable<MapSwitchEntity>().Where(row => row.Phase < (int)MapSwitchPhase.Completed)
            .FirstAsync(cancellationToken);
    private static MapSwitchSnapshot Snapshot(MapSwitchEntity row) => new(row.ReleaseId, row.MapId, row.VersionId,
        row.PreviousMapId, row.PreviousVersionId, (MapSwitchPhase)row.Phase, row.OperatorIdentity, row.Evidence,
        new DateTimeOffset(row.StartedAt), new DateTimeOffset(row.UpdatedAt));
    private static void Demand(TrafficUserContext user, TrafficPermission permission)
    { if (!user.Has(permission)) throw new UnauthorizedAccessException($"没有{permission}权限。"); }
}
