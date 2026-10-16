using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine($"PASS {message}"); }
var ct = CancellationToken.None;
var directory = Path.Combine(Path.GetTempPath(), $"agv-switch-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var database = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(directory, "test.db") });
try
{
    await new DatabaseInitializer(database).InitializeAsync(ct);
    var gate = new MapExecutionGate();
    var runtime = new ActiveMapRuntime();
    var resources = new TrafficResourceCatalog([]);
    var vehicles = new SwitchVehicleCache();
    var signals = new SwitchSignalCache();
    void Fresh() { vehicles.Items.Clear(); vehicles.Items.Add(SwitchVehicleCache.Ready()); signals.Items.Clear(); signals.Items.Add(SwitchSignalCache.Ready()); }
    Fresh();
    var settings = new MapSwitchSafetySettings { RequiredVehicleIds = ["AGV-01"], CheckPlcSignals = true };
    var switches = new MapSwitchService(database, gate, runtime, vehicles, signals, settings);
    var validator = new MapValidationService();
    var manager = new MapManagementService(database, validator, new MapImportService(), new CadUnderlayJobQueue(),
        new MapCalibrationService(), runtime, resources,
        new MapAssetStorageOptions { CachePath = directory, BackupPath = directory }, gate, switches);
    var editor = new TrafficUserContext("test\\engineer", "工程师", TrafficPermission.View | TrafficPermission.ManageMap);
    var viewer = new TrafficUserContext("test\\viewer", "观察员", TrafficPermission.View);
    Check(await gate.EnterAsync(MapExecutionOperation.NewWork, ct) is null, "初始化前禁止执行");
    await switches.InitializeAsync(ct);
    var created = await manager.CreateAsync(editor, new("Switch-Test", "安全切换测试", "", "LocalCartesian", "m", 1), ct);
    var workspace = created.Workspace!;
    var nodes = new List<MapNode> { new("A", 0, 0, MapNodeType.Station, true, [], "SITE-A"),
        new("B", 2, 0, MapNodeType.WaitingPoint, true, [], "SITE-B") };
    var definition = new MapDefinition("Switch-Test", "test", "LocalCartesian", "m", nodes,
        [new("AB", "A", "B", 2, 1, true, true, ["NARROW"])],
        [new("NARROW", TrafficResourceType.NarrowAisle)]);
    var immutable = new ActiveMapRuntimeSnapshot("map", "version", definition, DateTimeOffset.Now);
    nodes[0] = nodes[0] with { X = 100 };
    Check(immutable.Definition.Nodes[0].X == 0 && immutable.Graph.FindShortestRoute("A", "B") is not null,
        "运行快照复制源数据，图与定义一致");
    nodes[0] = nodes[0] with { X = 0 };
    var saved = await manager.SaveDraftAsync(editor, new(workspace.Project.MapId, workspace.Draft.VersionId,
        workspace.Project.Name, "", "", "LocalCartesian", "m", 1, definition, workspace.Draft.EditToken), ct);
    Check(saved.Accepted, "保存合法候选地图");
    var published = await manager.PublishAsync(editor, new(workspace.Project.MapId, workspace.Draft.VersionId), ct);
    Check(published.Accepted, "发布不可变候选版本");
    var target = published.Workspace!.Published!;
    var activate = new ActivateMapRequest(target.MapId, target.VersionId, "ACTIVATE Switch-Test", true, true, "已核对车辆和PLC，原现场仍兼容");
    Check(!(await manager.ActivateAsync(editor, activate, ct)).Accepted, "禁止绕过收尾流程直接激活");
    var denied = false;
    try { await switches.BeginAsync(viewer, new(target.MapId, target.VersionId), ct); }
    catch (UnauthorizedAccessException) { denied = true; }
    Check(denied, "观察员不能开始切换");

    // Begin must wait until an already admitted physical command operation has completed.
    var inFlight = await gate.EnterAsync(MapExecutionOperation.ExistingWork, ct);
    var begin = switches.BeginAsync(editor, new(target.MapId, target.VersionId), ct);
    await Task.Delay(50);
    Check(!begin.IsCompleted, "切换等待已进入的发送或PLC操作退出");
    inFlight!.Dispose();
    Check((await begin).Accepted, "记录切换并停止新增工作");
    Check(await gate.EnterAsync(MapExecutionOperation.NewWork, ct) is null, "收尾中禁止新派单");
    using (var lease = await gate.EnterAsync(MapExecutionOperation.ExistingWork, ct))
        Check(lease is not null, "收尾中允许原任务继续执行");
    Check(!(await switches.BeginAsync(editor, new(target.MapId, target.VersionId), ct)).Accepted, "不能并发发起第二次发布");
    var status = await switches.GetAsync(editor, ct);
    var releaseId = status.Current!.ReleaseId;
    Check(status.Current.PreviousVersionId is null && status.Mode == MapExecutionMode.Draining, "切换目标与原版本持久化");

    async Task<bool> Blocked(string word) => (await switches.GetBlockersAsync(definition, ct)).Any(item => item.Contains(word));
    vehicles.Items.Clear();
    Check(await Blocked("缺失"), "车辆缺失阻止切换"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { Connection = ConnectionState.Disconnected };
    Check(await Blocked("离线"), "离线车辆不能被跳过"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { LastSuccessfulPollAt = DateTimeOffset.Now.AddMinutes(-1) };
    Check(await Blocked("过期"), "旧定位状态不能作为确认依据"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { VelocityX = 0.2 };
    Check(await Blocked("静止"), "仍在运动的车辆禁止切图"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { VelocityX = null };
    Check(await Blocked("静止"), "速度缺失不能认定已停稳"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { Tasks = [new("old-task", 2, null)], NavigationState = "2" };
    Check(await Blocked("导航"), "控制器仍有旧任务时阻止切换"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { LocalizationConfidence = null };
    Check(await Blocked("置信度"), "缺失定位置信度时禁止切换"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { CurrentStation = "UNKNOWN" };
    Check(await Blocked("停靠"), "站点映射不兼容阻止切换"); Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { X = 1 };
    Check(await Blocked("停靠"), "路网内但没有安全停靠也不能切换"); Fresh();
    var moved = definition with { Nodes = definition.Nodes.Select(node => node.Id == "A" ? node with { X = -1 } : node).ToArray() };
    Check((await switches.GetBlockersAsync(moved, ct)).Any(item => item.Contains("停靠")), "站点移动后不会按旧站点坐标放行");
    signals.Items[0] = signals.Items[0] with { UpdatedAt = DateTimeOffset.Now.AddMinutes(-1) };
    Check(await Blocked("PLC输入失效"), "PLC陈旧状态阻止切换"); Fresh();
    signals.Items[0] = signals.Items[0] with { Value = true };
    Check(await Blocked("PLC工位故障"), "PLC故障阻止切换"); Fresh();

    await database.Insertable(new DispatchTaskEntity { TaskId = "QUEUED", StationId = "QUEUE", Stage = (int)DispatchTaskStage.WaitingForVehicle,
        CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync(ct);
    Check(!await Blocked("任务"), "未分配且未下发的排队任务不阻塞收尾");
    await database.Insertable(new DispatchTaskEntity { TaskId = "ACTIVE", StationId = "ACTIVE", VehicleId = "AGV-01",
        Stage = (int)DispatchTaskStage.PickingUp, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync(ct);
    Check(await Blocked("任务"), "取料或翻料中的任务不能切图");
    await database.Deleteable<DispatchTaskEntity>().Where(row => row.TaskId == "ACTIVE").ExecuteCommandAsync(ct);
    foreach (var commandStatus in new[] { AgvCommandStatus.Pending, AgvCommandStatus.Sending, AgvCommandStatus.RetryScheduled, AgvCommandStatus.Failed })
    {
        await database.Insertable(new AgvCommandOutboxEntity { CommandId = "OLD", IdempotencyKey = "OLD", TaskId = "OLD", StepId = "OLD",
            VehicleId = "AGV-01", Status = (int)commandStatus, PayloadJson = "{}", CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now, NextAttemptAt = DateTime.Now }).ExecuteCommandAsync(ct);
        Check(await Blocked("命令"), $"旧命令{commandStatus}阻止切换");
        Check(!(await manager.ActivateAsync(editor, activate, ct)).Accepted && runtime.Current is null, "旧命令存在时不改变运行指针");
        await database.Deleteable<AgvCommandOutboxEntity>().Where(row => row.CommandId == "OLD").ExecuteCommandAsync(ct);
    }
    var traffic = new TrafficResourceManager(database, new TrafficResourceCatalog(definition.Resources), new TrafficControlSettings(TimeSpan.FromSeconds(1)));
    Check((await traffic.TryAcquireAsync("STEP", "FAULTED", "AGV-01", ["NARROW"], ct)).Acquired, "建立物理窄道占用");
    await database.Updateable<TrafficResourceLeaseEntity>().SetColumns(row => row.ExpiresAt == DateTime.Now.AddMinutes(-1)).Where(row => row.ResourceId == "NARROW").ExecuteCommandAsync(ct);
    Check(await Blocked("物理资源"), "过期路权仍阻止切换，不能假定道路空闲");
    await database.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommandAsync(ct);
    await database.Insertable(new DispatchTaskStepEntity { StepId = "MANUAL", TaskId = "FAULTED", Status = (int)DispatchTaskStepStatus.ManualIntervention,
        CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync(ct);
    Check(await Blocked("步骤"), "异常未确认的动作步骤阻止切换");
    await database.Deleteable<DispatchTaskStepEntity>().ExecuteCommandAsync(ct);
    Check(!(await manager.ActivateAsync(editor, activate with { SiteSafetyConfirmed = false }, ct)).Accepted, "不能用软件状态代替现场确认");

    // Fail at the release-state write, after the map pointer/resource writes inside the same DB transaction.
    await database.Ado.ExecuteCommandAsync("CREATE TRIGGER fail_switch_commit BEFORE UPDATE ON map_switch WHEN NEW.Phase = 20 BEGIN SELECT RAISE(ABORT, 'injected'); END;");
    var failed = false;
    try { await manager.ActivateAsync(editor, activate, ct); } catch (InvalidOperationException) { failed = true; }
    Check(failed && gate.Mode == MapExecutionMode.Held, "事务异常保持暂停");
    Check(await database.Queryable<MapProjectEntity>().Where(row => row.IsActive).CountAsync(ct) == 0 && runtime.Current is null,
        "发布记录写入失败时地图和资源事务回滚");
    await database.Ado.ExecuteCommandAsync("DROP TRIGGER fail_switch_commit;");
    Fresh();
    Check((await manager.ActivateAsync(editor, activate, ct)).Accepted, "确认收尾后提交地图");
    Check(gate.Mode == MapExecutionMode.Held && runtime.Current?.VersionId == target.VersionId, "提交后不自动恢复派单");
    status = await switches.GetAsync(editor, ct);
    Check(status.Current!.Phase == MapSwitchPhase.AwaitingResume, "待放行阶段与地图提交一起保存");
    Check(!(await switches.CancelAsync(editor, new(releaseId, true, true, "取消"), ct)).Accepted, "提交后不能取消回旧指针");

    // Simulate a process crash after commit: new runtime/gate loads only durable DB state.
    var restartedGate = new MapExecutionGate();
    var restartedRuntime = new ActiveMapRuntime();
    var restartedResources = new TrafficResourceCatalog([]);
    var restartedSwitches = new MapSwitchService(database, restartedGate, restartedRuntime, vehicles, signals, settings);
    var loader = new ActiveMapLoader(NullLogger<ActiveMapLoader>.Instance, new DatabaseInitializer(database), database,
        restartedRuntime, restartedResources, restartedSwitches);
    await loader.StartAsync(ct);
    Check(restartedRuntime.Current?.VersionId == target.VersionId && restartedGate.Mode == MapExecutionMode.Held,
        "数据库提交后崩溃：重启加载正确版本并保持暂停");
    Check(await restartedGate.EnterAsync(MapExecutionOperation.ExistingWork, ct) is null, "中断发布恢复前禁止旧命令和PLC动作");
    Fresh(); vehicles.Items[0] = vehicles.Items[0] with { Connection = ConnectionState.Disconnected };
    Check(!(await restartedSwitches.ResumeAsync(editor, new(releaseId, true, true, "现场确认"), ct)).Accepted, "重启后离线车辆仍不能放行");
    Fresh();
    Check(!(await restartedSwitches.ResumeAsync(editor, new("stale-release", true, true, "现场确认"), ct)).Accepted, "拒绝过期切换编号");
    Check((await restartedSwitches.ResumeAsync(editor, new(releaseId, true, true, "重新采集并核对所有车辆和PLC"), ct)).Accepted,
        "现场重新核对后明确放行");
    Check(restartedGate.Mode == MapExecutionMode.Running, "放行后恢复运行");

    using (var occupied = await restartedGate.EnterControlAsync(ct))
    {
        using var cancellation = new CancellationTokenSource(30);
        var cancelled = false;
        try { await restartedGate.EnterAsync(MapExecutionOperation.NewWork, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "等待执行控制可取消，不泄漏占用");
    }
    using (var afterCancel = await restartedGate.EnterAsync(MapExecutionOperation.NewWork, ct))
        Check(afterCancel is not null, "取消等待后其他操作仍可进入");
    var nextWorkspace = published.Workspace!;
    var nextSaved = await manager.SaveDraftAsync(editor, new(nextWorkspace.Project.MapId, nextWorkspace.Draft.VersionId,
        nextWorkspace.Project.Name, "下一版", "", "LocalCartesian", "m", 1,
        definition with { Edges = definition.Edges.Select(edge => edge with { SpeedLimit = 0.5 }).ToArray() },
        nextWorkspace.Draft.EditToken), ct);
    var nextPublished = await manager.PublishAsync(editor, new(nextWorkspace.Project.MapId, nextWorkspace.Draft.VersionId), ct);
    Check(nextSaved.Accepted && nextPublished.Accepted, "活动版本保持固定，允许准备下一版");
    var nextVersion = nextPublished.Workspace!.Published!;
    Check((await restartedSwitches.BeginAsync(editor, new(nextVersion.MapId, nextVersion.VersionId), ct)).Accepted,
        "开始第二次切换，记录原活动版本");
    await restartedSwitches.InitializeAsync(ct);
    var interrupted = await restartedSwitches.GetAsync(editor, ct);
    Check(interrupted.Mode == MapExecutionMode.Held && interrupted.Current!.PreviousVersionId == target.VersionId,
        "收尾阶段重启也暂停，不能自动重放旧动作");
    Fresh();
    Check(!(await restartedSwitches.ContinueDrainAsync(editor, new(interrupted.Current!.ReleaseId, true, true,
        "尚未核对流程"), ct)).Accepted, "恢复收尾必须明确核对任务和PLC节点");
    vehicles.Items[0] = vehicles.Items[0] with { Connection = ConnectionState.Disconnected };
    Check(!(await restartedSwitches.ContinueDrainAsync(editor, new(interrupted.Current.ReleaseId, true, true,
        "已核对", true), ct)).Accepted, "离线时不能人工按钮绕过恢复检查");
    Fresh();
    vehicles.Items[0] = vehicles.Items[0] with { NavigationState = "2", Tasks = [new("OLD", 2, null)], VelocityX = 0.1 };
    await database.Insertable(new DispatchTaskEntity { TaskId = "RECOVER", StationId = "RECOVER", VehicleId = "AGV-01",
        Stage = (int)DispatchTaskStage.GoingToPickup, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync(ct);
    Check((await restartedSwitches.ContinueDrainAsync(editor, new(interrupted.Current.ReleaseId, true, true,
        "核对原运行地图、原任务和动作结果，可按原步骤继续收尾", true), ct)).Accepted,
        "有原任务时，经明确现场核对可恢复收尾");
    Check(restartedGate.Mode == MapExecutionMode.Draining && await restartedGate.EnterAsync(MapExecutionOperation.NewWork, ct) is null,
        "恢复原任务仍不允许新增派单");
    Check(await database.Queryable<DispatchTaskEntity>().Where(row => row.TaskId == "RECOVER" &&
        row.Stage == (int)DispatchTaskStage.GoingToPickup).CountAsync(ct) == 1, "恢复收尾不重置原任务步骤");
    await database.Deleteable<DispatchTaskEntity>().Where(row => row.TaskId == "RECOVER").ExecuteCommandAsync(ct);
    await restartedSwitches.InitializeAsync(ct);
    Fresh();
    Check((await restartedSwitches.CancelAsync(editor, new(interrupted.Current!.ReleaseId, true, true,
        "重启后确认现场已安全收尾，保留原地图"), ct)).Accepted, "收尾中断后核对安全状态可取消并保留原地图");
    Check(restartedRuntime.Current?.VersionId == target.VersionId, "取消未提交切换不会改变原运行版本");

    // Verify the real command worker cannot send while held, even when the queue contains a due old command.
    await database.Insertable(new DispatchTaskEntity { TaskId = "SEND", StationId = "SEND", VehicleId = "AGV-01",
        Stage = (int)DispatchTaskStage.WaitingForVehicle, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync(ct);
    await database.Insertable(new DispatchTaskStepEntity { TaskId = "SEND", StepId = "SEND-STEP", StepType = (int)DispatchTaskStepType.GoToPickup, Status = (int)DispatchTaskStepStatus.CommandPending,
        CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync(ct);
    var outbox = new AgvCommandOutboxRepository(database);
    // Prepare from Ready state through the production repository to use the actual payload and idempotency key.
    await database.Updateable<DispatchTaskStepEntity>().SetColumns(row => row.Status == (int)DispatchTaskStepStatus.Ready)
        .Where(row => row.StepId == "SEND-STEP").ExecuteCommandAsync(ct);
    Check(await outbox.TryPrepareTransportStepAsync("SEND", "SEND-STEP", "AGV-01", ["SITE-A", "SITE-B"], null, ct), "建立真实待发送命令");
    restartedGate.SetMode(MapExecutionMode.Held);
    var gateway = new CountingGateway();
    var worker = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,
        new AgvCommandOutboxOptions { PollIntervalMs = 100 }, outbox, gateway, restartedGate);
    await worker.StartAsync(ct);
    await Task.Delay(300);
    Check(gateway.Sends == 0, "实际发送服务在暂停状态不发送旧运动命令");
    using (var control = await restartedGate.EnterControlAsync(ct)) restartedGate.SetMode(MapExecutionMode.Draining);
    for (var attempt = 0; attempt < 20 && gateway.Sends == 0; attempt++) await Task.Delay(50);
    await worker.StopAsync(ct);
    Check(gateway.Sends == 1, "允许收尾时原命令仅发送一次");
    Check((await switches.GetAsync(editor, ct)).History.Any(item => item.Phase == MapSwitchPhase.Completed && item.Evidence.Contains("重新采集")),
        "切换历史保留确认人员和依据");
    Console.WriteLine($"Map switch smoke tests passed: {checks} checks.");
}
finally
{
    database.Ado.Close(); database.Dispose();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(directory, true); } catch (IOException) { Console.WriteLine("测试临时数据库保留：" + directory); }
}

sealed class CountingGateway : IAgvGateway
{
    public int Sends;
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DeviceConnectionStatus("fleet", "fleet", ConnectionState.Online, DateTimeOffset.Now));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([]);
    public Task<string> CreateTransportTaskAsync(string id, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    { Interlocked.Increment(ref Sends); return Task.FromResult(id); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => Task.FromResult<AgvTransportTaskStatus?>(null);
    public Task CancelTaskAsync(string id, CancellationToken ct) => Task.CompletedTask;
}
