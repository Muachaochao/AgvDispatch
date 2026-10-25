using System.Net.Http.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class BusinessRunPort(IManualTaskCreationService manual, ITaskCenterQueryService tasks,
    IPlcSignalCache signals, IAgvStateCache vehicles, IActiveMapRuntime map, IMapExecutionGate gate,
    TaskParameterRuntime parameters, DispatchTaskCreationOptions creation, DispatchOrchestratorOptions dispatch,
    DispatchTaskStepOptions steps, PlcCycleTestOptions isolated, IAgvGateway gateway,
    IRoutePlanner planner, ITrafficResourceManager resources, IRobokitTaskMapPreflight preflight,
    BusinessStandbyChains chains, ITrafficResourceControlService traffic, ISingleNavigationTestService navigation, bool simulatorOnly,
    IManualTaskCreationRepository receipts) : IBusinessRunPort
{
    private static readonly HttpClient lab = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(5), BaseAddress = new Uri("http://127.0.0.1:10201/") };
    private static string StandbyId(BusinessRunRecord run) => "STANDBY-" + (run.PendingRequestId ?? run.RunId);
    public async Task<BusinessRunRecord> ReconcileAsync(BusinessRunRecord run, CancellationToken ct)
    {
        if (run.PendingRequestId is null || run.TaskId is not null || run.VendorTaskId is not null) return run;
        var receipt = await receipts.FindRequestAsync(run.PendingRequestId, ct);
        if (receipt is not null)
        {
            if (receipt.RequestedVehicleId != run.Plan.VehicleId || receipt.MapId != run.MapId ||
                receipt.MapVersionId != run.MapVersionId || receipt.ParameterRevision != run.ParameterRevision ||
                !run.Plan.Stations.Contains(receipt.StationId)) throw new InvalidDataException("原创建回执与运行身份不符。");
            return run with { TaskId = receipt.TaskId };
        }
        var chain = chains.Find(StandbyId(run));
        if (chain is not null)
        {
            if (chain.VehicleId != run.Plan.VehicleId) throw new InvalidDataException("原待机链车辆身份不符。");
            // Derive only from the original durable chain. ObserveAsync checks the complete
            // vehicle task chain; absence/404 is never treated as permission to send again.
            return run with { VendorTaskId = chain.BusinessTaskId };
        }
        return run;
    }
    private static bool Fresh(DateTimeOffset? time) => time is { } t && t <= DateTimeOffset.UtcNow.AddSeconds(1) && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(5);
    private static bool Stopped(AgvRobotSnapshot v) => v.VelocityX is { } x && Math.Abs(x) < .01 &&
        v.VelocityY is { } y && Math.Abs(y) < .01 && v.AngularVelocity is { } a && Math.Abs(a) < .01 &&
        v.Tasks.All(t => t.Status is 4 or 5 or 6 or 7);
    private string? ConfigurationBlock() => isolated.Enabled ? "请退出单工位空载联调模式，使用正式业务启动配置；两种执行器不能同时运行。" :
        !creation.Enabled || !dispatch.Enabled || !steps.Enabled ? "请在启动配置中启用业务创建、调度及步骤执行；页面不会自动提升许可。" :
        creation.AutomaticCreationEnabled ? "本入口统一接续呼叫，请先在启动配置将 TaskCreation:AutomaticCreationEnabled 设为 false，保留 TaskCreation:Enabled=true。" :
        !gate.CanStartNewWork ? gate.NewWorkBlockReason : dispatch.GetConfigurationError();
    private MapNode Resolve(string site)
    {
        var nodes = map.Current?.Definition.Nodes.Where(n => n.Enabled && (n.Id == site || n.AgvSiteId == site)).ToArray() ?? [];
        return nodes.Length == 1 && !string.IsNullOrWhiteSpace(nodes[0].AgvSiteId) ? nodes[0]
            : throw new InvalidOperationException("启用站点映射不唯一或缺少厂家编号：" + site);
    }
    public async Task ValidateAsync(BusinessRunStart plan, CancellationToken ct)
    {
        if (ConfigurationBlock() is { } block) throw new InvalidOperationException(block);
        if (plan.Mode == "Cycle" && !simulatorOnly) throw new InvalidOperationException("循环测试只允许127.0.0.1:10200模拟PLC。");
        var options = await manual.GetOptionsAsync(ct);
        if (ConfigurationError(options, plan.Stations) is { } configurationError)
            throw new InvalidOperationException(configurationError);
        var standby = Resolve(plan.StandbySite);
        await CheckEmptyAsync(plan.VehicleId, ct);
        var config = parameters.Capture();
        if (config.WaitingSite == plan.StandbySite || config.DumpingSite == plan.StandbySite || config.Pickups.Any(p => p.PickupSite == plan.StandbySite))
            throw new InvalidOperationException("待机点必须独立于取放箱、倒料及倒料等待点。");
        foreach (var pickup in config.Pickups.Where(p => plan.Stations.Contains(p.StationId)))
        {
            var node = Resolve(pickup.PickupSite);
            if (planner.Plan(standby.AgvSiteId!, node.AgvSiteId!, new HashSet<string>()) is null ||
                planner.Plan(node.AgvSiteId!, standby.AgvSiteId!, new HashSet<string>()) is null)
                throw new InvalidOperationException("待机点与工位往返路线不连通：" + pickup.StationId);
        }
    }
    public static string? ConfigurationError(ManualTaskCreationOptions options, IReadOnlyList<string> stations)
    {
        var errors = stations.Select(id =>
        {
            var choice = options.Stations.SingleOrDefault(s => s.StationId == id);
            return choice is null ? id + "：未配置该工位的取货站点映射。" :
                choice.ConfigurationValid ? null : id + "：" + choice.Explanation;
        }).Where(error => error is not null).ToArray();
        if (errors.Length == 0) return null;
        var message = "工位配置校验未通过：\n" + string.Join("\n", errors);
        if (message.Contains("DumperExit", StringComparison.Ordinal))
            message += "\n配置位置：当前发布包 Service/appsettings.json → HandshakeExecution → DumperExit。ResourceIds 对应地图编辑中的共享区域资源ID和已核定边界；BodyProfiles 填写实测车辆及最大载箱包络。当前网页暂无这两项编辑表单，配置修改后需受控重启并重新完成启动恢复核对。";
        return message;
    }
    private async Task<bool> HasActiveAsync(CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            var result = await tasks.QueryAsync(new(TaskListFilter.All, "", page, 100), ct);
            if (result.Items.Any(t => t.Stage is not (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled))) return true;
            if (page * 100 >= result.TotalCount) return false;
            if (page >= 10000) throw new InvalidOperationException("任务总量过大，无法证明无未完成任务。");
        }
    }
    public async Task<BusinessRunObservation> ObserveAsync(BusinessRunRecord run, CancellationToken ct)
    {
        var currentMap = map.Current;
        string? block = ConfigurationBlock();
        vehicles.TryGet(run.Plan.VehicleId, out var v);
        if (v is null || !Fresh(v.LastSuccessfulPollAt) || v.Connection != ConnectionState.Online || v.Quality != SignalQuality.Good ||
            v.Blocked is not false || v.EmergencyStopped is not false || v.LocalizationConfidence is not >= .5 ||
            v.Alarms.Any(a => a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal)) block ??= "车辆实时状态、定位或健康状态未就绪。";
        var rows = signals.GetAll();
        bool Value(string station, string name, bool expected)
        {
            var matches = rows.Where(r => r.StationId == station && r.VariableName == name).ToArray();
            if (matches.Length != 1 || matches[0].Quality != SignalQuality.Good || !Fresh(matches[0].UpdatedAt) || matches[0].Value is null)
            { block ??= $"PLC信号缺失或不新鲜：{station}/{name}"; return false; }
            return matches[0].Value == expected;
        }
        var pickups = parameters.Capture().Pickups;
        var calls = pickups.Where(p => Value(p.StationId, "FullBoxRequested", true)).Select(p => p.StationId).ToArray();
        var closed = true;
        foreach (var p in pickups)
        {
            if (!Value(p.StationId, "Faulted", false) || !Value(p.StationId, "CommunicationOnline", true)) block ??= "工位故障或通讯无效：" + p.StationId;
            foreach (var name in new[] { "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced" })
                closed &= Value(p.StationId, name, false);
        }
        var dumper = parameters.Capture().DumperStationId;
        if (!Value(dumper, "Faulted", false)) block ??= "倒料工位故障。";
        foreach (var name in new[] { "AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumperReady", "DumpingCompletedMayLeave" }) closed &= Value(dumper, name, false);
        TaskCenterDetail? detail = run.TaskId is null ? null : await tasks.GetDetailAsync(run.TaskId, ct);
        if (detail is not null) closed &= detail.Resources.Count == 0 && detail.Commands.All(c => c.Status is AgvCommandStatus.Sent or AgvCommandStatus.Cancelled);
        var occupied = await traffic.GetResourcesAsync(ct);
        closed &= occupied.SelectMany(r => r.Leases).All(l => l.TaskId == StandbyId(run));
        var returned = false;
        if (run.VendorTaskId is { } id)
        {
            await resources.RenewTaskAsync(StandbyId(run), ct);
            var original = await gateway.GetTransportTaskStatusAsync(id, ct);
            if (original is null || original.VendorTaskId != id || original.VehicleId != run.Plan.VehicleId || !Fresh(original.UpdatedAt))
                block ??= "原待机导航身份或状态不可核对。";
            else if (original.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled or AgvTransportTaskState.Suspended)
                block ??= "原待机导航未正常完成：" + original.State;
            else returned = original.State == AgvTransportTaskState.Completed;
        }
        var site = v?.CurrentStation;
        if (v is not null && Stopped(v) && site is not null)
        {
            var node = Resolve(site);
            if (v.X is not { } x || v.Y is not { } y || Math.Sqrt(Math.Pow(x-node.X,2)+Math.Pow(y-node.Y,2)) > .3)
            { site = null; block ??= "车辆报告站点与实际坐标不符，不能确认安全停靠。"; }
        }
        return new(currentMap?.MapId ?? "", currentMap?.VersionId ?? "", parameters.Revision, block,
            calls, closed, await HasActiveAsync(ct), site, v is not null && Stopped(v), detail?.Task.Stage.ToString(), returned);
    }
    public async Task<string> CreateAsync(BusinessRunRecord run, string station, string requestId, CancellationToken ct)
    {
        var result = await manual.CreateAsync(TrafficUserContext.LocalOperation(run.Operator),
            new(requestId, station, run.Plan.VehicleId, 50, run.MapId, run.MapVersionId) { ParameterRevision = run.ParameterRevision }, ct);
        if (!result.Accepted || result.TaskId is null) throw new InvalidOperationException(result.Message);
        return result.TaskId;
    }
    public async Task<string> ReturnAsync(BusinessRunRecord run, CancellationToken ct)
    {
        using var lease = await gate.EnterAsync(MapExecutionOperation.NewWork, ct)
            ?? throw new InvalidOperationException(gate.NewWorkBlockReason);
        var observed = await ObserveAsync(run, ct);
        if (observed.Block is not null || observed.AnyActiveTasks || !observed.AllClosed || !observed.Stopped || observed.Calls.Length != 0 ||
            observed.MapId != run.MapId || observed.MapVersionId != run.MapVersionId || observed.ParameterRevision != run.ParameterRevision)
            throw new InvalidOperationException(observed.Block ?? "返回待机前出现新任务、呼叫或握手未闭合，未发送车辆。");
        var pinned = map.Current!;
        await CheckEmptyAsync(run.Plan.VehicleId, ct);
        var unavailable = await resources.GetUnavailableResourceIdsAsync(StandbyId(run), ct);
        var route = planner.Plan(observed.CurrentSite!, run.Plan.StandbySite, unavailable)
            ?? throw new InvalidOperationException("待机路径不可达或交通资源占用。");
        var path = route.Nodes.Select(id => Resolve(id).AgvSiteId!).ToArray();
        var checkedMap = await preflight.CheckCurrentMapAsync(run.Plan.VehicleId, run.MapId, run.MapVersionId, ct);
        var checkedRoute = await preflight.CheckRouteAsync(run.Plan.VehicleId, path, ct);
        if (!checkedMap.Accepted || !checkedRoute.Accepted || !ReferenceEquals(pinned, map.Current))
            throw new InvalidOperationException("待机路线车端核验失败：" + checkedMap.Message + checkedRoute.Message);
        if (!(await resources.TryAcquireAsync(StandbyId(run), StandbyId(run), run.Plan.VehicleId, route.ResourceIds, ct)).Acquired)
            throw new InvalidOperationException("待机路线资源尚未取得。");
        var latest = await ObserveAsync(run, ct);
        if (latest.Block is not null || latest.Calls.Length != 0 || latest.AnyActiveTasks || !latest.AllClosed || !latest.Stopped || latest.CurrentSite != observed.CurrentSite)
            throw new InvalidOperationException("发送前现场状态变化，保留待机意图和路权待核对，未发送车辆。");
        // Intent already persisted by BusinessRunService. On timeout retain reservation; never retry.
        var chain = new AgvTransportChain(StandbyId(run), run.Plan.VehicleId, path, 1, true);
        if (chains.Find(chain.BusinessTaskId) is not null) throw new InvalidOperationException("原待机任务已存在发送意图，禁止重复发送。");
        chains.Save(chain);
        var id = await gateway.CreateTransportTaskAsync(chain.BusinessTaskId, run.Plan.VehicleId, path, new AgvTransportAction("Wait"), ct);
        chains.Save(chain with { MaySend = false });
        return id;
    }
    public async Task ReleaseReturnAsync(BusinessRunRecord run, CancellationToken ct)
    { await resources.ReleaseTaskAsync(StandbyId(run), ct); }
    private async Task CheckEmptyAsync(string vehicleId, CancellationToken ct)
    {
        var originalNavigation = await navigation.ReadAsync(ct);
        if (originalNavigation.Current is { } n && n.State is not ("Completed" or "Failed" or "Cancelled"))
            throw new InvalidOperationException("原单车导航未结束或结果不确定，请先核对原任务。");
        var jack = await navigation.ReadJackAsync(ct);
        if (jack.Current is { } j && j.State is not ("Completed" or "ObservedTarget" or "Rejected"))
            throw new InvalidOperationException("原顶升操作尚未核对完成。");
        if (jack.VehicleId != vehicleId || jack.Telemetry is not { State: 3, Enabled: true, HasLoad: false, Emergency: false, ErrorCode: 0 } t || !Fresh(t.ObservedAt))
            throw new InvalidOperationException("回待机前需核对指定车辆顶升下降到位且无载荷。");
    }
    public async Task SimulatorAsync(BusinessRunRecord run, bool stop, CancellationToken ct)
    {
        if (!simulatorOnly) throw new InvalidOperationException("循环呼叫不能连接正式PLC。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "cycle")
        { Content = JsonContent.Create(new { runId = run.RunId, stations = run.Plan.Stations, rounds = run.Plan.Rounds, stop }) };
        request.Headers.Add("X-Agv-Lab-Control", "business-run-v1");
        using var response = await lab.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("模拟PLC循环控制未接受：" + await response.Content.ReadAsStringAsync(ct));
    }
}
