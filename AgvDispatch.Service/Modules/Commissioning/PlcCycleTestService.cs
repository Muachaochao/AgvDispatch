using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using Microsoft.Extensions.Hosting;

namespace AgvDispatch.Service;

/// <summary>A single explicitly armed empty-vehicle simulator cycle. It never creates a production
/// dispatch task, certifies physical dumper exit, or retries an uncertain write/motion.</summary>
public sealed partial class PlcCycleTestService : BackgroundService, IPlcCycleTestService
{
    private readonly PlcCycleTestOptions _options;
    private readonly IPlcCycleTestPort _port;
    private readonly ISingleNavigationTestService _navigation;
    private readonly string _path;
    private readonly IActiveMapRuntime? _map;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private PlcCycleTestRecord? _record;
    private static readonly string[] PickupSignals = ["FullBoxRequested", "BoxPresent", "Faulted", "CommunicationOnline", "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced"];
    private static readonly string[] DumperSignals = ["AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumperReady", "DumpingCompletedMayLeave", "Faulted"];
    private static readonly string[] PickupOutputs = ["FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced"];
    private static bool Terminal(string stage) => stage is "Completed" or "Disarmed" or "Hold";

    public PlcCycleTestService(PlcCycleTestOptions options, IPlcCycleTestPort port,
        ISingleNavigationTestService navigation, string storagePath, IActiveMapRuntime? map = null)
    {
        _options = options; _port = port; _navigation = navigation;
        _path = Path.GetFullPath(storagePath); _map = map;
        if (options.MaximumAgeSeconds is < 1 or > 30 || options.StageTimeoutSeconds is < 10 or > 3600)
            throw new ArgumentException("PLC空载联调采样有效期或阶段超时无效。");
        if (!File.Exists(_path)) return;
        _record = JsonSerializer.Deserialize<PlcCycleTestRecord>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("PLC空载联调记录损坏，拒绝继续。");
        if (!_record.SimulationOnly || _record.PhysicalExitVerified || !Guid.TryParseExact(_record.CycleId, "N", out _))
            throw new InvalidDataException("PLC空载联调记录身份无效。");
        if (!Terminal(_record.Stage))
            Hold("后台重启：原阶段可能包含未确定的动作；保留原任务、PLC输出和机械状态，禁止自动续跑或重发。");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await ProcessAsync(stoppingToken);
    }
    private static void Authorize(TrafficUserContext user)
    {
        if (!user.Has(TrafficPermission.Administer)) throw new UnauthorizedAccessException("需要管理员现场操作权限。");
    }
    private bool Fresh(DateTimeOffset? time) => time is { } value && value <= DateTimeOffset.UtcNow.AddSeconds(1)
        && DateTimeOffset.UtcNow - value <= TimeSpan.FromSeconds(_options.MaximumAgeSeconds);
    private bool Is(PlcCycleTestEnvironment environment, string station, string signal, bool value) =>
        environment.Signals.Single(s => s.StationId == station && s.VariableName == signal).Value == value;
    private string? EnvironmentBlock(PlcCycleTestEnvironment environment, bool requireStopped, bool baseline)
    {
        if (!_options.Enabled) return "PLC呼叫空载联调未启用。";
        if (!environment.SimulatorOnly) return "只允许回环地址的独立模拟PLC；正式PLC禁止使用本入口。";
        if (!environment.BusinessExecutionDisabled) return "正式任务创建、调度、握手与命令队列必须全部关闭。";
        if (!environment.MapRunning || string.IsNullOrWhiteSpace(environment.MapId) || string.IsNullOrWhiteSpace(environment.MapVersionId)) return "运行地图未就绪或正在切换。";
        if (!Fresh(environment.ObservedAt) || environment.VehicleId != _options.VehicleId || !environment.VehicleOnline || !environment.VehicleHealthy || !environment.VehicleLocalized)
            return "测试车辆离线、状态过期、故障或定位无效。";
        if (baseline && !environment.VehicleEmpty) return "本入口仅允许无箱空载车辆，不得用于真实取箱或倒料。";
        if (requireStopped && !environment.VehicleStopped) return "车辆尚未确认停稳。";
        // Between stations the vehicle legitimately reports no current station. Original
        // navigation must still be queried so a failed task cannot be hidden by this value.
        if ((baseline || requireStopped) && string.IsNullOrWhiteSpace(environment.CurrentStation)) return "车辆当前真实站点未知。";
        if (environment.Signals.Count != 21 || environment.Signals.Any(s => s.Quality != "Good" || s.Value is null || !Fresh(s.ObservedAt)))
            return "模拟PLC的21个信号必须全部Good且新鲜。";
        foreach (var pair in PickupSignals.Select(signal => (_options.StationId, signal)).Concat(DumperSignals.Select(signal => (_options.DumperStationId, signal))))
            if (environment.Signals.Count(s => s.StationId == pair.Item1 && s.VariableName == pair.signal) != 1)
                return "来源工位或倒料机信号缺失或重复。";
        if (!Is(environment, _options.StationId, "Faulted", false) || !Is(environment, _options.StationId, "CommunicationOnline", true) || !Is(environment, _options.DumperStationId, "Faulted", false))
            return "来源工位/倒料机故障或工位通讯异常。";
        if (_record is { } record && !Terminal(record.Stage) && (environment.MapId != record.MapId || environment.MapVersionId != record.MapVersionId ||
            environment.RobotMap != record.RobotMap || environment.RobotMapMd5 != record.RobotMapMd5)) return "本轮运行地图或车端地图身份已经变化。";
        if (baseline)
        {
            if (!Is(environment, _options.StationId, "BoxPresent", true)) return "模拟工位箱体在位信号必须先置1；车辆现场仍须空载。";
            if (PickupOutputs.Any(signal => !Is(environment, _options.StationId, signal, false)) ||
                new[] { "AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumperReady", "DumpingCompletedMayLeave" }.Any(signal => !Is(environment, _options.DumperStationId, signal, false)))
                return "本轮PLC基线尚未闭合：全部H、Ready、Done应为0。";
        }
        return null;
    }
    private PlcCycleTestStatus Status(string? block = null) => new(_options.Enabled,
        _options.Enabled && (_record is null || _record.Stage is "Completed" or "Disarmed") && _record?.PendingWrite is null && block is null,
        block ?? (_record?.Stage == "Hold" ? _record.Message : _record is { } r && !Terminal(r.Stage) ? "本轮已经武装或执行中，不能重复启动。" : null),
        _options.VehicleId, _options.StationId, _record?.PickupSite ?? _options.PickupSite,
        _record?.WaitingSite ?? _options.WaitingSite, _record?.DumpingSite ?? _options.DumpingSite,
        "仅独立模拟PLC与无箱车辆联调；一次武装只执行一轮。联调收尾不证明真实倒料区已退出，不计正式28阶段验收。", _record);
    public async Task<PlcCycleTestStatus> ReadAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if (!_options.Enabled) return Status("PLC呼叫空载联调未启用。");
            if (_record is not null && !Terminal(_record.Stage)) return Status();
            if (_record?.Stage == "Hold") return Status();
            try { return Status(EnvironmentBlock(await _port.ObserveAsync(ct), true, true)); }
            catch (Exception e) when (!ct.IsCancellationRequested) { return Status("现场状态不可用：" + e.Message); }
        }
        finally { _mutex.Release(); }
    }
    public async Task<PlcCycleTestStatus> ArmAsync(TrafficUserContext user, PlcCycleTestArmRequest request, CancellationToken ct)
    {
        Authorize(user); await _mutex.WaitAsync(ct);
        try
        {
            if (_record?.RequestId == request.RequestId)
            {
                var requestedDefinition = _map?.Current?.Definition;
                if (!MapRouteSiteMapper.IsSameNode(requestedDefinition,
                        _record.PickupSite, request.PickupSite) ||
                    !MapRouteSiteMapper.IsSameNode(requestedDefinition,
                        _record.WaitingSite, request.WaitingSite) ||
                    !MapRouteSiteMapper.IsSameNode(requestedDefinition,
                        _record.DumpingSite, request.DumpingSite))
                    throw new InvalidOperationException("同一请求编号不能用于其他路线。");
                return Status();
            }
            if (_record is not null && _record.Stage is not ("Completed" or "Disarmed")) throw new InvalidOperationException("原联调仍在执行或已冻结；不得覆盖原记录或重复发车。");
            if (_record?.PendingWrite is not null) throw new InvalidOperationException("原PLC写入结果未决，禁止重新武装覆盖。");
            var history = _path + ".history";
            if (Directory.Exists(history))
                foreach (var file in Directory.EnumerateFiles(history, "*.json"))
                {
                    var previous = JsonSerializer.Deserialize<PlcCycleTestRecord>(File.ReadAllText(file))
                        ?? throw new InvalidDataException("历史联调记录损坏，拒绝重新武装。");
                    if (previous.RequestId == request.RequestId)
                        throw new InvalidOperationException($"该请求编号属于历史联调 {previous.CycleId}，不得重放为新一轮。");
                }
            if (!Guid.TryParseExact(request.RequestId, "N", out _) || !request.SiteClearConfirmed || !request.EmptyVehicleConfirmed || !request.SimulatorOnlyConfirmed)
                throw new ArgumentException("须明确确认现场区域、车辆无箱空载及仅使用独立模拟PLC。");
            var sites = new[] { request.PickupSite, request.WaitingSite, request.DumpingSite };
            if (sites.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128 || s != s.Trim() || s.Any(char.IsControl)) || sites.Distinct(StringComparer.Ordinal).Count() != 3)
                throw new ArgumentException("取料、等待、倒料三个真实站点必须有效且互不相同。");
            var environment = await _port.ObserveAsync(ct);
            if (EnvironmentBlock(environment, true, true) is { } block) throw new InvalidOperationException(block);
            var nav = await _navigation.ReadAsync(ct);
            if (!nav.Enabled || !nav.Ready || nav.Current is { State: not ("Completed" or "Failed" or "Cancelled") })
                throw new InvalidOperationException("原导航未结束或独立导航不可用，请先查询原任务。");
            var jack = await _navigation.ReadJackAsync(ct);
            if (!jack.Ready || jack.Telemetry is not { State: 3, Enabled: true, Emergency: false, ErrorCode: 0, HasLoad: false } t || !Fresh(t.ObservedAt))
                throw new InvalidOperationException("需确认顶升下降到位且未检测到载荷；不自动降低未核对的机构。");
            var proof = await _port.CheckRoutesAsync(new(_options.VehicleId, environment.CurrentStation!, request.PickupSite, request.WaitingSite, request.DumpingSite), ct);
            if (!Fresh(proof.ObservedAt) || proof.RobotMap != environment.RobotMap || proof.RobotMapMd5 != environment.RobotMapMd5 || string.IsNullOrWhiteSpace(proof.RobotMap) ||
                proof.RobotMapMd5.Length != 32 || proof.RobotMapMd5.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("车端路线核验与当前地图身份不一致。");
            var definition = _map?.Current?.Definition;
            foreach (var pair in new[] { (environment.CurrentStation!, request.PickupSite), (request.PickupSite, request.WaitingSite), (request.WaitingSite, request.DumpingSite), (request.DumpingSite, request.PickupSite) }
                         .Where(p => !MapRouteSiteMapper.IsSameNode(definition, p.Item1, p.Item2)))
                if (!proof.Routes.Any(r =>
                        MapRouteSiteMapper.IsSameNode(definition, r.SourceSite, pair.Item1) &&
                        MapRouteSiteMapper.IsSameNode(definition, r.TargetSite, pair.Item2) &&
                        r.Stations.Count >= 2 &&
                        MapRouteSiteMapper.IsSameNode(definition, r.Stations[0], pair.Item1) &&
                        MapRouteSiteMapper.IsSameNode(definition, r.Stations[^1], pair.Item2)))
                    throw new InvalidOperationException($"车端路线未核验通过：{pair.Item1} → {pair.Item2}。");
            var fresh = await _port.ObserveAsync(ct);
            if (EnvironmentBlock(fresh, true, true) is { } freshBlock) throw new InvalidOperationException(freshBlock);
            if (fresh.MapId != environment.MapId ||
                fresh.MapVersionId != environment.MapVersionId ||
                fresh.RobotMap != proof.RobotMap ||
                fresh.RobotMapMd5 != proof.RobotMapMd5 ||
                !MapRouteSiteMapper.IsSameNode(definition,
                    fresh.CurrentStation, environment.CurrentStation))
                throw new InvalidOperationException("武装期间地图或车辆站点变化，请重新核对。");
            ArchiveCurrent();
            var now = DateTimeOffset.UtcNow;
            var callAlreadyHigh = Is(environment, _options.StationId, "FullBoxRequested", true);
            _record = new(Guid.NewGuid().ToString("N"), request.RequestId, _options.VehicleId, _options.StationId,
                request.PickupSite, request.WaitingSite, request.DumpingSite, environment.MapId, environment.MapVersionId,
                proof.RobotMap, proof.RobotMapMd5, "ArmedAwaitCall", callAlreadyHigh
                    ? "已武装一轮；检测到当前满箱呼叫为1，将立即受理并等待PLC撤呼叫。"
                    : "已武装一轮；请手动将本工位满箱呼叫从0置1。", user.WindowsIdentity, now, now, [], []);
            Event("武装记录已保存；本轮映射和3项现场确认已固定。最终实际倒料区退出核验不适用于此联调。");
            return Status();
        }
        finally { _mutex.Release(); }
    }
    public async Task<PlcCycleTestStatus> StopAsync(TrafficUserContext user, PlcCycleTestStopRequest request, CancellationToken ct)
    {
        Authorize(user); await _mutex.WaitAsync(ct);
        try
        {
            if (_record is null || _record.CycleId != request.CycleId || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 1000) throw new ArgumentException("请提供原联调编号与至少10字的冻结原因。");
            if (_record.Stage == "ArmedAwaitCall") Transition("Disarmed", "已撤销本轮武装；未发送车辆或PLC动作。" + request.Reason);
            else if (!Terminal(_record.Stage)) Hold("人工冻结后续推进；不取消车辆已接受的动作、不清PLC输出：" + request.Reason);
            return Status();
        }
        finally { _mutex.Release(); }
    }
    public async Task<PlcCycleTestStatus> ResetAsync(TrafficUserContext user, PlcCycleTestResetRequest request, CancellationToken ct)
    {
        Authorize(user);
        await _mutex.WaitAsync(ct);
        try
        {
            if (_record is null || _record.CycleId != request.CycleId)
                throw new ArgumentException("没有匹配的原联调记录。");
            if (_record.VehicleId != _options.VehicleId || _record.StationId != _options.StationId)
                throw new InvalidOperationException("当前启动配置与原联调身份不一致，禁止复位。");
            if (_record.Stage is not ("Hold" or "Completed" or "Disarmed"))
                throw new InvalidOperationException("本轮仍在执行；只能核对状态，不能重置原记录。");
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 1000)
                throw new ArgumentException("请填写至少10字的现场重置依据。");
            var environment = await _port.ObserveAsync(ct);
            var block = EnvironmentBlock(environment, requireStopped: true, baseline: false);
            if (block is not null) throw new InvalidOperationException(block);
            var definition = _map?.Current?.Definition;
            if (!MapRouteSiteMapper.IsSameNode(
                    definition, environment.CurrentStation, _record.PickupSite))
                throw new InvalidOperationException("车辆未返回本轮取料站，不能重置记录。");
            if (!environment.VehicleEmpty)
                throw new InvalidOperationException("车辆顶升未下降到位或载荷状态异常，不能重置记录。");
            if (_record.PendingWrite is not null)
                throw new InvalidOperationException("原PLC写入结果尚未核清，禁止用重置覆盖未决写入记录。");
            if (!Signal(environment, _options.StationId, "FullBoxRequested", false) ||
                !Signal(environment, _options.DumperStationId, "DumperReady", false) ||
                !Signal(environment, _options.DumperStationId, "DumpingCompletedMayLeave", false))
                throw new InvalidOperationException("重置前请先将满箱呼叫、Ready、Done全部清0。");
            var navigation = await _navigation.ReadAsync(ct);
            var jack = await _navigation.ReadJackAsync(ct);
            if (!navigation.Ready || navigation.Current is { State: not ("Completed" or "Failed" or "Cancelled") } ||
                !jack.Ready || jack.Current is { State: not ("Completed" or "Rejected") })
                throw new InvalidOperationException("原导航或顶升动作未明确结束，不能重置记录。");
            foreach (var (station, signal) in new[] {
                (_options.StationId, "FullBoxRequestAcknowledged"),
                (_options.StationId, "FullBoxRemoved"),
                (_options.StationId, "EmptyBoxTaskAcknowledged"),
                (_options.StationId, "EmptyBoxPlaced"),
                (_options.DumperStationId, "AgvAtWaitingPosition"),
                (_options.DumperStationId, "AgvAtDumpingPosition")
            })
            {
                if (Signal(environment, station, signal, true))
                {
                    // Persist before every reset write too; a lost response must never
                    // permit a subsequent reset to blindly repeat the operation.
                    _record = _record with { PendingWrite = new(station, signal, false, "Disarmed", DateTimeOffset.UtcNow, false) };
                    Persist();
                    await _port.WriteAsync(station, signal, false, ct);
                    var readback = await _port.ObserveAsync(ct);
                    if (EnvironmentBlock(readback, true, false) is { } resetBlock || !Signal(readback, station, signal, false))
                        throw new InvalidOperationException("重置写入后状态或回读无效，保留未决记录。");
                    _record = _record with { PendingWrite = null };
                    Event($"重置回读确认：{station}/{signal}=0。");
                }
            }
            var after = await _port.ObserveAsync(ct);
            if (EnvironmentBlock(after, true, false) is { } afterBlock) throw new InvalidOperationException(afterBlock);
            foreach (var (station, signal) in new[] {
                (_options.StationId, "FullBoxRequestAcknowledged"),
                (_options.StationId, "FullBoxRemoved"),
                (_options.StationId, "EmptyBoxTaskAcknowledged"),
                (_options.StationId, "EmptyBoxPlaced"),
                (_options.DumperStationId, "AgvAtWaitingPosition"),
                (_options.DumperStationId, "AgvAtDumpingPosition")
            })
                if (!Signal(after, station, signal, false))
                    throw new InvalidOperationException($"重置回读不一致：{station}/{signal}。");
            _record = _record with {
                Stage = "Disarmed",
                PendingWrite = null,
                Message = "上一轮记录已安全重置，H输出已回读为0；可开始新一轮。",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Event("现场重置：" + request.Reason.Trim());
            return Status();
        }
        finally { _mutex.Release(); }
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, _record); file.Flush(true); }
        File.Move(temporary, _path, true);
    }
    private void ArchiveCurrent()
    {
        if (_record is null) return;
        var directory = _path + ".history";
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, _record.CycleId + ".json");
        // Never overwrite the evidence of a previous cycle.
        if (File.Exists(destination)) return;
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, _record);
        file.Flush(true);
    }
    private void Event(string message)
    {
        var record = _record!;
        _record = record with { Events = record.Events.Append(new(DateTimeOffset.UtcNow, record.Stage, message)).ToArray() };
        Persist();
    }
    private void Transition(string stage, string message)
    {
        _record = _record! with { Stage = stage, Message = message, UpdatedAt = DateTimeOffset.UtcNow };
        Event(message);
    }
    private void Hold(string reason) => Transition("Hold", reason + " 保留原任务及输出，禁止自动重试。");

    private static TrafficUserContext SystemOperator =>
        TrafficUserContext.LocalOperation("plc-cycle-test");

    private bool Signal(PlcCycleTestEnvironment environment, string station, string signal, bool value)
    {
        var rows = environment.Signals.Where(item =>
            item.StationId == station && item.VariableName == signal).ToArray();
        return rows.Length == 1 && rows[0].Value == value;
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if (_record is null || Terminal(_record.Stage)) return;
            if (DateTimeOffset.UtcNow - _record.UpdatedAt > TimeSpan.FromSeconds(_options.StageTimeoutSeconds))
            {
                Hold($"阶段 {_record.Stage} 超过 {_options.StageTimeoutSeconds} 秒未完成。");
                return;
            }
            var environment = await _port.ObserveAsync(ct);
            var block = EnvironmentBlock(environment,
                requireStopped: !_record.Stage.StartsWith("AwaitNavigation", StringComparison.Ordinal), baseline: false);
            if (block is not null)
            {
                Hold("现场状态失效，需重新核对：" + block);
                return;
            }

            switch (_record.Stage)
            {
                case "ArmedAwaitCall":
                    if (!Signal(environment, _options.StationId, "FullBoxRequested", true)) return;
                    await WriteOnceAsync(_options.StationId, "FullBoxRequestAcknowledged", true,
                        "AwaitCallCleared", "已受理CNC-001满箱呼叫，等待PLC撤下呼叫信号。", ct);
                    break;
                case "AwaitCallCleared":
                    if (!Signal(environment, _options.StationId, "FullBoxRequested", false)) return;
                    if (MapRouteSiteMapper.IsSameNode(
                            _map?.Current?.Definition, environment.CurrentStation, _record.PickupSite))
                        Transition("JackUpStart", "车辆已在LM2，省略空驶段，准备顶升。");
                    else
                        await StartNavigationAsync(_record.PickupSite, "AwaitNavigationToPickup", ct);
                    break;
                case "AwaitNavigationToPickup":
                    if (await NavigationCompletedAsync(_record.PickupSite, ct))
                        Transition("JackUpStart", "车辆已完成到LM2，准备顶升。");
                    break;
                case "JackUpStart":
                    await StartJackAsync("Up", "AwaitJackUp", ct);
                    break;
                case "AwaitJackUp":
                    if (await JackAtAsync(1, ct))
                        await WriteOnceAsync(_options.StationId, "FullBoxRemoved", true,
                            "MarkReturnResponsibility", "顶升到位；记录本轮取箱结果。", ct);
                    break;
                case "MarkReturnResponsibility":
                    await WriteOnceAsync(_options.StationId, "EmptyBoxTaskAcknowledged", true,
                        "NavigateWaiting", "记录本轮返箱责任，准备前往等待点。", ct);
                    break;
                case "NavigateWaiting":
                    if (MapRouteSiteMapper.IsSameNode(
                            _map?.Current?.Definition, environment.CurrentStation, _record.WaitingSite))
                        Transition("NotifyWaiting", "车辆已在LM5，通知倒料设备等待位申请。");
                    else
                        await StartNavigationAsync(_record.WaitingSite, "AwaitNavigationToWaiting", ct);
                    break;
                case "AwaitNavigationToWaiting":
                    if (await NavigationCompletedAsync(_record.WaitingSite, ct))
                        Transition("NotifyWaiting", "车辆已完成到LM5，准备申请倒料许可。");
                    break;
                case "NotifyWaiting":
                    if (!Signal(environment, _options.DumperStationId, "DumperReady", false))
                        throw new InvalidOperationException("发出等待申请前Ready应为0，不能沿用上一轮许可。");
                    await WriteOnceAsync(_options.DumperStationId, "AgvAtWaitingPosition", true,
                        "AwaitDumperReady", "车辆已到等待点，等待PLC给出Ready。", ct);
                    break;
                case "AwaitDumperReady":
                    if (!Signal(environment, _options.DumperStationId, "DumperReady", true)) return;
                    await WriteOnceAsync(_options.DumperStationId, "AgvAtWaitingPosition", false,
                        "AwaitWaitingCleared", "已观察Ready；撤销等待位申请。", ct);
                    break;
                case "AwaitWaitingCleared":
                    if (!Signal(environment, _options.DumperStationId, "AgvAtWaitingPosition", false) ||
                        !Signal(environment, _options.DumperStationId, "DumperReady", false)) return;
                    Transition("NavigateDumping", "等待位许可已闭合，准备进入倒料点。");
                    break;
                case "NavigateDumping":
                    if (MapRouteSiteMapper.IsSameNode(
                            _map?.Current?.Definition, environment.CurrentStation, _record.DumpingSite))
                        Transition("NotifyDumping", "车辆已在LM6，准备通知倒料周期。");
                    else
                        await StartNavigationAsync(_record.DumpingSite, "AwaitNavigationToDumping", ct);
                    break;
                case "AwaitNavigationToDumping":
                    if (await NavigationCompletedAsync(_record.DumpingSite, ct))
                        Transition("NotifyDumping", "车辆已完成到LM6，准备通知倒料周期。");
                    break;
                case "NotifyDumping":
                    if (!Signal(environment, _options.DumperStationId, "DumpingCompletedMayLeave", false))
                        throw new InvalidOperationException("通知倒料周期前Done应为0，不能沿用上一轮完成信号。");
                    await WriteOnceAsync(_options.DumperStationId, "AgvAtDumpingPosition", true,
                        "AwaitDone", "车辆已到LM6，等待PLC给出倒料完成Done。", ct);
                    break;
                case "AwaitDone":
                    if (!Signal(environment, _options.DumperStationId, "DumpingCompletedMayLeave", true)) return;
                    Transition("NavigateReturn", "已观察倒料完成Done，准备返回LM2。");
                    break;
                case "NavigateReturn":
                    await StartNavigationAsync(_record.PickupSite, "AwaitNavigationToReturn", ct);
                    break;
                case "AwaitNavigationToReturn":
                    if (await NavigationCompletedAsync(_record.PickupSite, ct))
                        Transition("ClearDumping", "车辆已返回LM2，准备关闭倒料周期。");
                    break;
                case "ClearDumping":
                    await WriteOnceAsync(_options.DumperStationId, "AgvAtDumpingPosition", false,
                        "AwaitDoneCleared", "车辆已返回取料工位，撤销倒料周期通知。", ct);
                    break;
                case "AwaitDoneCleared":
                    if (!Signal(environment, _options.DumperStationId, "DumpingCompletedMayLeave", false)) return;
                    Transition("JackDownStart", "倒料周期及完成信号已闭合，准备下降。");
                    break;
                case "JackDownStart":
                    await StartJackAsync("Down", "AwaitJackDown", ct);
                    break;
                case "AwaitJackDown":
                    if (await JackAtAsync(3, ct))
                        await WriteOnceAsync(_options.StationId, "EmptyBoxPlaced", true,
                            "ClearPicked", "顶升下降到到位；记录模拟空箱已放位。", ct);
                    break;
                case "ClearPicked":
                    await WriteOnceAsync(_options.StationId, "FullBoxRemoved", false,
                        "ClearPlaced", "清取箱结果保持通知。", ct);
                    break;
                case "ClearPlaced":
                    await WriteOnceAsync(_options.StationId, "EmptyBoxPlaced", false,
                        "ClearReturnResponsibility", "清空箱放到位通知。", ct);
                    break;
                case "ClearReturnResponsibility":
                    await WriteOnceAsync(_options.StationId, "EmptyBoxTaskAcknowledged", false,
                        "ClearCallAck", "清本轮返箱责任。", ct);
                    break;
                case "ClearCallAck":
                    await WriteOnceAsync(_options.StationId, "FullBoxRequestAcknowledged", false,
                        "Completed", "本轮空载PLC联调已完成；未据此放行正式28阶段生产流程。", ct);
                    break;
                default:
                    throw new InvalidOperationException("未知联调阶段：" + _record.Stage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (_record is not null && !Terminal(_record.Stage)) Hold("阶段处理失败：" + exception.Message);
        }
        finally { _mutex.Release(); }
    }

    private async Task WriteOnceAsync(string station, string signal, bool value, string nextStage, string message, CancellationToken ct)
    {
        _record = _record! with
        {
            PendingWrite = new(station, signal, value, nextStage, DateTimeOffset.UtcNow, false),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Persist();
        await _port.WriteAsync(station, signal, value, ct);
        var fresh = await _port.ObserveAsync(ct);
        if (EnvironmentBlock(fresh, false, false) is not null || !Signal(fresh, station, signal, value))
            throw new InvalidOperationException($"写入后回读不一致：{station}/{signal}，保持冻结。");
        _record = _record with { PendingWrite = null };
        Transition(nextStage, message);
    }

    private async Task StartNavigationAsync(string target, string nextStage, CancellationToken ct)
    {
        var environment = await _port.ObserveAsync(ct);
        if (EnvironmentBlock(environment, true, false) is { } block) throw new InvalidOperationException(block);
        var stations = await _navigation.ReadStationsAsync(ct);
        var preview = await _navigation.PreviewAsync(SystemOperator,
            new(_options.VehicleId, environment.CurrentStation!, target)
            {
                ExpectedRobotMap = stations.RobotMap,
                ExpectedRobotMapMd5 = stations.RobotMapMd5
            }, ct);
        _record = _record! with { Children = _record.Children.Append(new("Navigation", preview.PreviewId,
            null, target, "Sending", DateTimeOffset.UtcNow)).ToArray() };
        Persist();
        var record = await _navigation.SubmitAsync(SystemOperator,
            new NavigationTestSubmit(preview.PreviewId, true), ct);
        _record = _record! with
        {
            Children = _record.Children.Select(child => child.Kind == "Navigation" && child.RequestId == preview.PreviewId
                ? child with { RequestId = record.TaskId, VendorTaskId = record.VendorTaskId, State = record.State } : child).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Transition(nextStage, $"已下发 {preview.SourceSite}→{target}，等待车辆实际到站。");
    }

    private async Task<bool> NavigationCompletedAsync(string target, CancellationToken ct)
    {
        var status = await _navigation.ReadAsync(ct);
        var record = status.Current;
        var expected = _record!.Children.LastOrDefault(child => child.Kind == "Navigation");
        if (record is null || expected is null || record.TaskId != expected.RequestId || record.VendorTaskId != expected.VendorTaskId ||
            record.VehicleId != _record.VehicleId || record.RobotMap != _record.RobotMap || record.RobotMapMd5 != _record.RobotMapMd5 || record.TargetSite != target)
            throw new InvalidOperationException("原导航记录身份与目标站不一致，禁止继续。");
        if (record.State is "Failed" or "Cancelled" or "Uncertain")
            throw new InvalidOperationException($"原导航状态为 {record.State}：{record.Message}");
        if (record.State != "Completed") return false;
        _record = _record! with
        {
            Children = _record.Children.Select(child =>
                child.Kind == "Navigation" && child.RequestId == record.TaskId
                    ? child with { State = "Completed", UpdatedAt = DateTimeOffset.UtcNow }
                    : child).ToArray()
        };
        return true;
    }

    private async Task StartJackAsync(string action, string nextStage, CancellationToken ct)
    {
        var current = await _navigation.ReadJackAsync(ct);
        var targetState = action == "Up" ? 1 : 3;
        if (current.Telemetry?.State == targetState)
            throw new InvalidOperationException("顶升已被其他操作改变到目标状态，请核对原动作，禁止代入本轮结果。");
        if (current.Telemetry is not { Enabled: true, Emergency: false, ErrorCode: 0 } before || !Fresh(before.ObservedAt))
            throw new InvalidOperationException("顶升机构不可用或反馈异常。");
        var requestId = Guid.NewGuid().ToString("N");
        _record = _record! with { Children = _record.Children.Append(new("Jack", requestId, null,
            action, "Sending", DateTimeOffset.UtcNow)).ToArray() };
        Persist();
        var record = await _navigation.SubmitJackAsync(SystemOperator,
            new(requestId, action, true, true), ct);
        _record = _record! with
        {
            Children = _record.Children.Select(child => child.Kind == "Jack" && child.RequestId == requestId
                ? child with { State = record.State } : child).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Transition(nextStage, record.Message);
    }

    private async Task<bool> JackAtAsync(int state, CancellationToken ct)
    {
        var status = await _navigation.ReadJackAsync(ct);
        var telemetry = status.Telemetry;
        var expected = _record!.Children.LastOrDefault(child => child.Kind == "Jack");
        if (expected is null || status.Current is not { } original || original.RequestId != expected.RequestId ||
            original.VehicleId != _record.VehicleId || original.Action != expected.Target)
            throw new InvalidOperationException("顶升原动作身份与本轮不一致。");
        if (original.State is "Rejected" or "Uncertain" or "ObservedTarget" || !original.Acknowledged)
            throw new InvalidOperationException("顶升原动作未明确应答或结果不确定，禁止重发或冒认完成。");
        if (telemetry is null || !Fresh(telemetry.ObservedAt) || telemetry.Enabled == false || telemetry.Emergency || telemetry.ErrorCode != 0)
            throw new InvalidOperationException(status.BlockReason ?? "顶升反馈异常。");
        if (telemetry.State == state && original.State == "Completed")
        {
            _record = _record! with
            {
                Children = _record.Children.Select(child =>
                    child.Kind == "Jack" && child.RequestId == original.RequestId
                        ? child with { State = "Completed", UpdatedAt = DateTimeOffset.UtcNow }
                        : child).ToArray()
            };
            return true;
        }
        if (status.Current?.State is "Rejected" or "Uncertain")
            throw new InvalidOperationException("顶升原动作被拒绝或结果不确定，禁止重发。");
        return false;
    }
}
