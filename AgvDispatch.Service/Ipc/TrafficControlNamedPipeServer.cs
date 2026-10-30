using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class TrafficControlNamedPipeServer(
    ILogger<TrafficControlNamedPipeServer> logger,
    TrafficSecurityOptions options,
    IDatabaseInitializer database,
    ITrafficResourceControlService resources,
    ITrafficFlowMonitor trafficFlow,
    IManualReleaseWorkflowService workflow,
    IAlarmService alarms,
    IAgvTrackStore tracks,
    IMapManagementService maps,
    IMapSwitchService mapSwitches,
    IExecutionRecoveryService recovery,
    ISystemStatusSnapshotService systemStatus,
    IDeviceMonitoringService deviceMonitoring,
    ITaskCenterQueryService tasks,
    IManualTaskCreationService taskCreation,
    ITaskParameterService taskParameters,
    ITaskCancellationService taskCancellation,
    ITaskHistoryExportService historyExports,
    ICommunicationSettingsService communicationSettings,
    NamedPipeDiagnostics pipeDiagnostics,
    ITrafficGeometryPreviewService? geometry=null,
    IRobokitInspectionService? robokit=null, IRobokitInspectionHistory? robokitHistory=null,IMapTransferLedger? mapTransfers=null, ITaskHandshakeStore? handshakes=null, ITaskDiagnosticArchiveService? diagnosticExports=null, ICommissioningQueryService? commissioning=null,
    ITrafficWaitingBayDepartureService? waitingBayDeparture=null, ISingleNavigationTestService? navigationTest=null, IRobotMapDownloadService? robotMapDownload=null,
    IPlcCycleTestService? plcCycleTest=null, IBusinessRunService? businessRun=null, IFleetTestService? fleetTest=null, IChargingService? charging=null,
    IRoadClosureService? roadClosures=null) : IsolatedBackgroundService
{
    private async Task<RobokitInspectionSnapshot> InspectRobokitAsync(TrafficUserContext user,RobokitInspectionQuery query,CancellationToken ct)
    {
        var result=await (robokit??throw new NotSupportedException("后台未配置Robokit只读核验。")).ReadAsync(query,ct);
        if(robokitHistory is not null) await robokitHistory.AppendAsync(user.WindowsIdentity,result,ct);
        return result;
    }
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        if(roadClosures is not null) await roadClosures.InitializeAsync(cancellationToken);
        if(mapTransfers is not null)await mapTransfers.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    // Map drafts can contain hundreds of stations and thousands of edges.
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("交通控制Named Pipe已启动。Pipe={PipeName}", options.PipeName);
        // Bound accepted requests and await their disposal at shutdown.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptLoopAsync(stoppingToken)));
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                // Accept the next request immediately. A dashboard poll or status read must
                // never occupy the only pipe instance while an operator submits an action.
                await HandleAcceptedConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "交通控制Named Pipe连接处理失败：{Error}", exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task HandleAcceptedConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            pipeDiagnostics.ConnectionOpened();
            try
            {
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "交通控制Named Pipe连接处理失败：{Error}", exception.Message);
            }
            finally
            {
                pipeDiagnostics.ConnectionClosed();
            }
        }
    }

    private NamedPipeServerStream CreateServerPipe()
    {
        var security = new PipeSecurity();
        // Additional instances need CreateNewInstance; ReadWrite alone is insufficient
        // for a non-administrator service owner.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            options.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            0,
            0,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var identityName = "(unresolved)";
        string? operation = null;
        string? requestId = null;
        TrafficControlPipeResponse response;
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var request = await ReadAsync<TrafficControlPipeRequest>(pipe, readTimeout.Token);
            operation = request.Operation;
            if (request.Payload.ValueKind == JsonValueKind.Object &&
                (request.Payload.TryGetProperty("requestId", out var id) || request.Payload.TryGetProperty("RequestId", out id)) &&
                id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsedId))
                requestId = parsedId.ToString("N");
            // Windows only exposes the client token after at least one read from the pipe.
            identityName = pipe.GetImpersonationUserName();
            // No software login or role separation. Keep the OS identity only for operation auditing.
            var user = TrafficUserContext.LocalOperation(identityName);
            if (!operation.StartsWith("Get", StringComparison.Ordinal))
                logger.LogInformation("交通控制请求开始。Identity={Identity}, Operation={Operation}, RequestId={RequestId}",
                    identityName, operation, requestId);
            response = await DispatchAsync(user, request, cancellationToken);
            // Snapshot polling is not a business operation and must not fill the log.
            var logLevel = !response.Success ? LogLevel.Warning
                : request.Operation.StartsWith("Get", StringComparison.Ordinal) ? LogLevel.Debug
                : LogLevel.Information;
            logger.Log(logLevel,
                "交通控制请求处理完成，等待写回回执。Identity={Identity}, Operation={Operation}, RequestId={RequestId}, Success={Success}, ElapsedMs={ElapsedMs}",
                identityName, request.Operation, requestId, response.Success, elapsed.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "拒绝或处理交通控制请求失败。Identity={Identity}, Operation={Operation}, RequestId={RequestId}, ElapsedMs={ElapsedMs}",
                identityName, operation, requestId, elapsed.Elapsed.TotalMilliseconds);
            response = Failure(exception is UnauthorizedAccessException
                ? exception.Message
                : $"后台处理失败：{exception.Message}");
            if (exception is UnauthorizedAccessException) response = response with { ErrorCode = "Unauthorized" };
        }
        pipeDiagnostics.RecordRequest(identityName, operation, response.Success);
        try { await WriteAsync(pipe, response, cancellationToken); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            logger.LogWarning(exception,
                "交通控制回执未送达，不代表后台处理已撤销；先查询原申请，不重复提交。Identity={Identity}, Operation={Operation}, RequestId={RequestId}, ProcessingSuccess={Success}, ElapsedMs={ElapsedMs}",
                identityName, operation, requestId, response.Success, elapsed.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private async Task<TrafficControlPipeResponse> DispatchAsync(
        TrafficUserContext user,
        TrafficControlPipeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Operation == TrafficControlOperations.GetSession)
        {
            return Success(new TrafficControlSession(
                user.WindowsIdentity, user.DisplayName, user.Permissions) { Role = user.Role });
        }
        if (!user.Has(TrafficPermission.View))
        {
            throw new UnauthorizedAccessException(
                $"账号{user.WindowsIdentity}没有交通资源查看权限。");
        }

        if (request.Operation == "SetVehicleConnection")
        {
            var q=Deserialize<SetVehicleConnectionRequest>(request.Payload);
            if (charging?.OwnsVehicle(q.VehicleId) == true)
                throw new InvalidOperationException("车辆充电会话尚未闭合，请先停止充电并核对离位后再更改连接。");
            if (!user.Has(TrafficPermission.ManageConnections)) throw new UnauthorizedAccessException("无车辆连接管理权限。");
            if (navigationTest is not null && (await navigationTest.ReadAsync(cancellationToken)) is { Enabled:true } n &&
                n.VehicleId.Equals(q.VehicleId,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该车处于独立导航或升降测试模式，请先结束原测试并退出该模式，再改变连接。");
            if (businessRun is not null && (await businessRun.ReadAsync(cancellationToken)).Current is { Stage:not "Completed" } b &&
                b.Plan.VehicleId.Equals(q.VehicleId,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该车原业务运行尚未闭合，请先停止新增并核对收尾。");
            if (fleetTest is not null && (await fleetTest.ReadAsync(cancellationToken)) is { Current: { Stage:not "Completed" } f } &&
                f.Plan.VehicleIds.Contains(q.VehicleId,StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("该车参与的双车测试尚未闭合，请先停止新增并核对原记录。");
        }
        if (request.Operation is "SubmitForkTest" or "SubmitJackTest" or "PreviewNavigationTest" or "SubmitNavigationTest" or "ResumeNavigationTest" or "PreviewNavigationTestDetour" or "ConfirmNavigationTestDetour" or "ContinueNavigationTestDetour" &&
            plcCycleTest is not null && (await plcCycleTest.ReadAsync(cancellationToken)).Enabled)
            throw new InvalidOperationException("PLC空载联调模式独占测试车辆；手动导航或顶升请先停止该模式并明确切换启动方式。");
        if (request.Operation is "PreviewNavigationTest" or "SubmitNavigationTest" or "ResumeNavigationTest" or "PreviewNavigationTestDetour" or "ConfirmNavigationTestDetour" or "ContinueNavigationTestDetour" &&
            businessRun is not null && (await businessRun.ReadAsync(cancellationToken)).Current is { Stage: not "Completed" } retainedRun)
        {
            // Only the explicitly enabled forklift test may coexist with another parked
            // vehicle's retained responsibility. The service rechecks its owner live;
            // this does not close the run, release resources or resume business execution.
            await (navigationTest ?? throw new InvalidOperationException("业务运行尚未完成待机收尾。"))
                .ValidateRetainedBusinessRunAsync(retainedRun.Plan.VehicleId, cancellationToken);
        }

        return request.Operation switch
        {
            "GetRoadClosures" => Success(await (roadClosures??throw new NotSupportedException("后台未更新道路封锁管理。")).ListAsync(cancellationToken)),
            "CreateRoadClosure" => Success(await (roadClosures??throw new NotSupportedException("后台未更新道路封锁管理。")).CreateAsync(user,Deserialize<RoadClosureCreateRequest>(request.Payload),cancellationToken)),
            "ReleaseRoadClosure" => Success(await (roadClosures??throw new NotSupportedException("后台未更新道路封锁管理。")).ReleaseAsync(user,Deserialize<RoadClosureReleaseRequest>(request.Payload),cancellationToken)),
            "PreviewRoadClosureRoute" => Success(await (roadClosures??throw new NotSupportedException("后台未更新道路封锁管理。")).PreviewAsync(Deserialize<RoadClosurePreviewRequest>(request.Payload),cancellationToken)),
            "PreviewNavigationTestDetour" => Success(await (navigationTest??throw new NotSupportedException("后台未更新受控改道。")).PreviewDetourAsync(user,Deserialize<NavigationTestDetourQuery>(request.Payload),cancellationToken)),
            "ConfirmNavigationTestDetour" => Success(await (navigationTest??throw new NotSupportedException("后台未更新受控改道。")).ConfirmDetourAsync(user,Deserialize<NavigationTestDetourConfirm>(request.Payload),cancellationToken)),
            "ContinueNavigationTestDetour" => Success(await (navigationTest??throw new NotSupportedException("后台未更新受控改道。")).ContinueDetourAsync(user,Deserialize<NavigationTestDetourContinue>(request.Payload),cancellationToken)),
            "GetCharging" => Success(await (charging??throw new NotSupportedException("后台未更新充电调度。")).ReadAsync(cancellationToken)),
            "StopCharging" => Success(await (charging??throw new NotSupportedException("后台未更新充电调度。")).RequestStopAsync(user,Deserialize<ChargingStopRequest>(request.Payload),cancellationToken)),
            "ResolveCharging" => Success(await (charging??throw new NotSupportedException("后台未更新充电调度。")).ResolveHoldAsync(user,Deserialize<ChargingResolveRequest>(request.Payload),cancellationToken)),
            "GetForkTest" => Success(await (navigationTest??throw new NotSupportedException("货叉后台未更新。")).ReadForkAsync(cancellationToken)),
            "SubmitForkTest" => Success(await (navigationTest??throw new NotSupportedException("货叉后台未更新。")).SubmitForkAsync(user,Deserialize<ForkTestSubmit>(request.Payload),cancellationToken)),
            "StopForkTest" => Success(await (navigationTest??throw new NotSupportedException("货叉后台未更新。")).SubmitForkAsync(user,RequireForkStop(request.Payload),cancellationToken)),
            "GetBusinessRun" => Success(await (businessRun??throw new NotSupportedException("后台未更新持续运行入口。")).ReadAsync(cancellationToken)),
            "GetFleetTest" => Success(await (fleetTest??throw new NotSupportedException("后台未更新双车测试。")).ReadAsync(cancellationToken)),
            "StartFleetTest" => Success(await (fleetTest??throw new NotSupportedException("后台未更新双车测试。")).StartAsync(user,Deserialize<FleetTestStart>(request.Payload),cancellationToken)),
            "RetireFleetTestStart" => Success(await (fleetTest??throw new NotSupportedException("后台未更新多车测试。")).RetireStartAsync(user,Deserialize<FleetTestStartRetirement>(request.Payload),cancellationToken)),
            "StopFleetTest" => Success(await (fleetTest??throw new NotSupportedException("后台未更新双车测试。")).ControlAsync(user,"stop",Deserialize<FleetTestControl>(request.Payload),cancellationToken)),
            "ResumeFleetTest" => Success(await (fleetTest??throw new NotSupportedException("后台未更新双车测试。")).ControlAsync(user,"resume",Deserialize<FleetTestControl>(request.Payload),cancellationToken)),
            "CloseFleetTest" => Success(await (fleetTest??throw new NotSupportedException("后台未更新双车测试。")).ControlAsync(user,"close",Deserialize<FleetTestControl>(request.Payload),cancellationToken)),
            "ReconcileFleetTest" => Success(await (fleetTest??throw new NotSupportedException("后台未更新多车测试。")).ControlAsync(user,"reconcile",Deserialize<FleetTestControl>(request.Payload),cancellationToken)),
            "StartBusinessRun" => Success(await (businessRun??throw new NotSupportedException("后台未更新持续运行入口。")).StartAsync(user,Deserialize<BusinessRunStart>(request.Payload),cancellationToken)),
            "StopBusinessRun" => Success(await (businessRun??throw new NotSupportedException("后台未更新持续运行入口。")).StopAsync(user,Deserialize<BusinessRunStop>(request.Payload),cancellationToken)),
            "ResumeBusinessRun" => Success(await (businessRun??throw new NotSupportedException("后台未更新持续运行入口。")).ResumeAsync(user,Deserialize<BusinessRunResume>(request.Payload),cancellationToken)),
            "CloseReviewedBusinessRun" => Success(await (businessRun??throw new NotSupportedException("后台未更新持续运行入口。")).CloseReviewedAsync(user,Deserialize<BusinessRunStop>(request.Payload),cancellationToken)),
            "GetPlcCycleTest" => Success(await (plcCycleTest??throw new NotSupportedException("PLC空载联调后台未更新。")).ReadAsync(cancellationToken)),
            "ArmPlcCycleTest" => Success(await (plcCycleTest??throw new NotSupportedException("PLC空载联调后台未更新。")).ArmAsync(user,Deserialize<PlcCycleTestArmRequest>(request.Payload),cancellationToken)),
            "StopPlcCycleTest" => Success(await (plcCycleTest??throw new NotSupportedException("PLC空载联调后台未更新。")).StopAsync(user,Deserialize<PlcCycleTestStopRequest>(request.Payload),cancellationToken)),
            "ResetPlcCycleTest" => Success(await (plcCycleTest??throw new NotSupportedException("PLC空载联调后台未更新。")).ResetAsync(user,Deserialize<PlcCycleTestResetRequest>(request.Payload),cancellationToken)),
            "GetCurrentRobotMapDownload" => Success(await (robotMapDownload??throw new NotSupportedException("请更新车端地图只读下载后台。")).ReadCurrentAsync(Deserialize<RobotMapDownloadQuery>(request.Payload),cancellationToken)),
            "GetJackTest" => Success(await (navigationTest??throw new NotSupportedException("顶升测试后台未更新。")).ReadJackAsync(cancellationToken)),
            "SubmitJackTest" => Success(await (navigationTest??throw new NotSupportedException("顶升测试后台未更新。")).SubmitJackAsync(user,Deserialize<JackTestSubmit>(request.Payload),cancellationToken)),
            "GetNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).ReadAsync(cancellationToken)),
            "GetNavigationTestStations" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).ReadStationsAsync(cancellationToken)),
            "PreviewNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).PreviewAsync(user,Deserialize<NavigationTestQuery>(request.Payload),cancellationToken)),
            "SubmitNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).SubmitAsync(user,Deserialize<NavigationTestSubmit>(request.Payload),cancellationToken)),
            "CancelNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).CancelAsync(user,Deserialize<NavigationTestCancel>(request.Payload),cancellationToken)),
            "ResumeNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).ResumeAsync(user,Deserialize<NavigationTestResume>(request.Payload),cancellationToken)),
            "GetCommissioningReport" => Success(await (commissioning??throw new NotSupportedException("当前后台未提供联调检查，请部署匹配版本。")).ReadAsync(user,Deserialize<CommissioningQuery>(request.Payload),cancellationToken)),
            "GetTaskRuntimeEvents" => Success(await (handshakes??throw new NotSupportedException("后台未配置运行事件查询。")).QueryAsync(Deserialize<TaskRuntimeQuery>(request.Payload),cancellationToken)),
            "ExportTaskDiagnostics" => Success(await (diagnosticExports??throw new NotSupportedException("后台未配置联调诊断导出。")).ExportAsync(Deserialize<TaskDetailRequest>(request.Payload).TaskId,cancellationToken)),
            "CancelDispatchTask" => Success(await taskCancellation.RequestAsync(user, Deserialize<TaskCancelRequest>(request.Payload), cancellationToken)),
            "BeginTaskRecoveryReview" => Success(await recovery.BeginReviewAsync(user, Deserialize<TaskRecoveryReviewRequest>(request.Payload), cancellationToken)),
            "GetExecutionRecovery" => Success(await recovery.GetAsync(cancellationToken)),
            "DecideExecutionRecovery" => Success(await recovery.DecideAsync(user, Deserialize<RecoveryDecisionRequest>(request.Payload), cancellationToken)),
            "ReleaseExecutionRecovery" => Success(await recovery.ReleaseAsync(user, Deserialize<RecoveryReleaseRequest>(request.Payload), cancellationToken)),
            "SetVehicleConnection" => Success(await communicationSettings.SetVehicleConnectionAsync(user,
                Deserialize<SetVehicleConnectionRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetCommunicationSettings => Success(await communicationSettings.GetAsync(user, cancellationToken)),
            TrafficControlOperations.SaveCommunicationSettings => Success(await communicationSettings.SaveAsync(user,
                Deserialize<SaveCommunicationSettingsRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.ProbeConnection => Success(await communicationSettings.ProbeAsync(user,
                Deserialize<ConnectionProbeRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTasks =>
                Success(await tasks.QueryAsync(Deserialize<TaskListQuery>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTaskRoutePreview => Success(await taskCreation.PreviewRoutesAsync(Deserialize<ManualTaskRoutePreviewRequest>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetTaskCreationOptions => Success(await taskCreation.GetOptionsAsync(cancellationToken)),
            TrafficControlOperations.GetTaskParameters => Success(await taskParameters.GetAsync(cancellationToken)),
            TrafficControlOperations.ValidateTaskParameters => Success(await taskParameters.ValidateAsync(Deserialize<TaskParameterRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.SaveTaskParameters => Success(await taskParameters.SaveAsync(user, Deserialize<TaskParameterRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.CreateTask => Success(await taskCreation.CreateAsync(user, Deserialize<ManualTaskCreationRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTaskHistoryExportProgress => Success(await historyExports.GetProgressAsync(user, Deserialize<TaskHistoryExportJobRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.CancelTaskHistoryExport => Success(await historyExports.CancelAsync(user, Deserialize<TaskHistoryExportJobRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTaskHistoryExportCache => Success(await historyExports.GetCacheAsync(user, Deserialize<TaskHistoryExportCacheRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.CleanupTaskHistoryExportCache => Success(await historyExports.CleanupAsync(user, Deserialize<TaskHistoryExportCacheRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.ExportTaskHistory =>
                Success(await historyExports.ExportAsync(user, Deserialize<TaskHistoryExportRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTaskHistory =>
                Success(await tasks.QueryHistoryAsync(Deserialize<TaskHistoryQuery>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTaskDetail =>
                Success(await tasks.GetDetailAsync(Deserialize<TaskDetailRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.ListMapTransfers=>Success(await (mapTransfers??throw new NotSupportedException("未配置地图传输记录。")).ListAsync(user,Deserialize<MapTransferListQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetMapTransferHistory=>Success(await (mapTransfers??throw new NotSupportedException("未配置地图传输记录。")).HistoryAsync(user,Deserialize<MapTransferHistoryQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetMapTransferReview=>Success(await (mapTransfers??throw new NotSupportedException("未配置地图传输记录。")).ReviewAsync(user,Deserialize<MapTransferQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.PrepareMapTransfer=>Success(await (mapTransfers??throw new NotSupportedException("未配置地图传输记录。")).PrepareAsync(user,Deserialize<PrepareMapTransferRequest>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetMapTransfer=>Success(await (mapTransfers??throw new NotSupportedException("未配置地图传输记录。")).ReadAsync(user,Deserialize<MapTransferQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.ObserveMapTransfer=>Success(await (mapTransfers??throw new NotSupportedException("未配置地图传输记录。")).ObserveAsync(user,Deserialize<MapTransferQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetRobokitInspectionHistory => Success(await (robokitHistory ?? throw new NotSupportedException("后台未配置核验记录。")).ReadAsync(Deserialize<RobokitInspectionHistoryQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetRobokitInspection => Success(await InspectRobokitAsync(user,Deserialize<RobokitInspectionQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetTrafficGeometryPreview => Success(await (geometry??throw new NotSupportedException("后台未配置几何只读预检。")).ReadAsync(Deserialize<TrafficGeometryPreviewQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetTrafficBlocking => Success(await resources.GetBlockingAsync(Deserialize<TrafficBlockingQuery>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetWaitingBayDeparture => Success(await (waitingBayDeparture??throw new NotSupportedException("后台未配置候车位离位只读核验。")).ReadAsync(Deserialize<TrafficWaitingBayDepartureQuery>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetTrafficWaiting => Success(await resources.GetWaitingAsync(Deserialize<TrafficWaitingQuery>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTrafficMap => Success(await resources.GetMapAsync(Deserialize<TrafficMapQuery>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetTrafficLifecycle => Success(await resources.GetLifecycleAsync(cancellationToken)),
            TrafficControlOperations.GetResources =>
                Success(await resources.GetResourcesAsync(cancellationToken)),
            TrafficControlOperations.GetTrafficFlow =>
                Success(trafficFlow.GetSnapshot()),
            TrafficControlOperations.GetSystemStatus =>
                Success(await systemStatus.GetAsync(user, cancellationToken)),
            TrafficControlOperations.GetDeviceMonitoring => Success(await deviceMonitoring.GetSnapshotAsync(cancellationToken)),
            TrafficControlOperations.GetAgvTrack =>
                Success(await tracks.QueryAsync(
                    Deserialize<AgvTrackQueryRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetAlarms =>
                Success(await GetAlarmsAsync(request.Payload, cancellationToken)),
            TrafficControlOperations.AcknowledgeAlarm =>
                Success(await alarms.AcknowledgeAsync(
                    user,
                    Deserialize<AcknowledgeAlarmRequest>(request.Payload),
                    cancellationToken)),
            "ArchiveMapProject" => Success(await maps.ArchiveAsync(user,Deserialize<ArchiveMapRequest>(request.Payload),cancellationToken)),
            TrafficControlOperations.GetMaps =>
                Success(await maps.GetMapsAsync(user, cancellationToken)),
            TrafficControlOperations.GetMapWorkspace =>
                Success(await maps.GetWorkspaceAsync(
                    user,
                    Deserialize<MapIdRequest>(request.Payload).MapId,
                    cancellationToken)),
            TrafficControlOperations.CreateMap =>
                Success(await maps.CreateAsync(
                    user,
                    Deserialize<CreateMapRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.SaveMapDraft =>
                Success(await maps.SaveDraftAsync(
                    user,
                    Deserialize<SaveMapDraftRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.PublishMap =>
                Success(await maps.PublishAsync(
                    user,
                    Deserialize<PublishMapRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.GenerateMapUnderlay =>
                Success(await maps.GenerateUnderlayAsync(
                    user,
                    Deserialize<GenerateMapUnderlayRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.SaveMapCalibration =>
                Success(await maps.SaveCalibrationAsync(
                    user,
                    Deserialize<SaveMapCalibrationRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.ReviewMapActivation => Success(await mapSwitches.ReviewActivationAsync(user, Deserialize<MapActivationReviewQuery>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetMapSwitch => Success(await mapSwitches.GetAsync(user, cancellationToken)),
            TrafficControlOperations.BeginMapSwitch => Success(await mapSwitches.BeginAsync(user,
                Deserialize<BeginMapSwitchRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.ContinueMapDrain => Success(await mapSwitches.ContinueDrainAsync(user,
                Deserialize<FinishMapSwitchRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.ResumeMapSwitch => Success(await mapSwitches.ResumeAsync(user,
                Deserialize<FinishMapSwitchRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.CancelMapSwitch => Success(await mapSwitches.CancelAsync(user,
                Deserialize<FinishMapSwitchRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.ActivateMap =>
                Success(await maps.ActivateAsync(
                    user,
                    Deserialize<ActivateMapRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.PreviewMapImport =>
                Success(await maps.PreviewImportAsync(
                    user,
                    Deserialize<PreviewMapImportRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.ExportMap =>
                Success(await maps.ExportAsync(
                    user,
                    Deserialize<ExportMapRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.GetMapAudits =>
                Success(await GetMapAuditsAsync(user, request.Payload, cancellationToken)),
            TrafficControlOperations.GetMapVersionHistory =>
                Success(await maps.GetVersionHistoryAsync(
                    user, Deserialize<MapIdRequest>(request.Payload).MapId, cancellationToken)),
            TrafficControlOperations.GetMapVersion =>
                Success(await GetMapVersionAsync(user, request.Payload, cancellationToken)),
            TrafficControlOperations.GetAudits =>
                Success(await resources.GetRecentAuditsAsync(
                    Deserialize<MaximumCountRequest>(request.Payload).MaximumCount,
                    cancellationToken)),
            TrafficControlOperations.GetReleaseRequests =>
                Success(await workflow.GetRequestsAsync(
                    user,
                    Deserialize<MaximumCountRequest>(request.Payload).MaximumCount,
                    cancellationToken)),
            TrafficControlOperations.SubmitRelease =>
                Success(await workflow.SubmitAsync(
                    user,
                    Deserialize<SubmitManualReleaseRequest>(request.Payload),
                    cancellationToken)),
            TrafficControlOperations.ReviewRelease =>
                Success(await workflow.ReviewAsync(
                    user,
                    Deserialize<ReviewManualReleaseRequest>(request.Payload),
                    cancellationToken)),
            _ => Failure($"不支持的操作：{request.Operation}")
        };
    }

    private static ForkTestSubmit RequireForkStop(JsonElement payload)
    {
        var request=Deserialize<ForkTestSubmit>(payload);
        return request.Action=="Stop"?request:throw new ArgumentException("停止接口只接受Stop。");
    }
    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions) ??
        throw new InvalidDataException($"请求参数{typeof(T).Name}为空。");

    private static TrafficControlPipeResponse Success<T>(T payload) =>
        new(true, null, JsonSerializer.SerializeToElement(payload, JsonOptions));

    private static TrafficControlPipeResponse Failure(string error) =>
        new(false, error, JsonSerializer.SerializeToElement(new { }, JsonOptions));

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new InvalidDataException($"Named Pipe帧长度非法：{length}。");
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions) ??
               throw new InvalidDataException("Named Pipe请求为空。");
    }

    private async Task<IReadOnlyList<AlarmSnapshot>> GetAlarmsAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<AlarmQueryRequest>(payload);
        return await alarms.GetRecentAsync(query.MaximumCount, query.ActiveOnly, cancellationToken);
    }

    private async Task<IReadOnlyList<MapOperationAuditSnapshot>> GetMapAuditsAsync(
        TrafficUserContext user,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<MapAuditQueryRequest>(payload);
        return await maps.GetAuditsAsync(user, query.MapId, query.MaximumCount, cancellationToken);
    }

    private async Task<MapVersionSnapshot?> GetMapVersionAsync(
        TrafficUserContext user,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<MapVersionRequest>(payload);
        return await maps.GetVersionAsync(user, query.MapId, query.VersionId, cancellationToken);
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var result = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        result.Converters.Add(new JsonStringEnumConverter());
        return result;
    }

    private sealed record MaximumCountRequest(int MaximumCount);
    private sealed record AlarmQueryRequest(int MaximumCount, bool ActiveOnly);
    private sealed record MapIdRequest(string MapId);
    private sealed record MapAuditQueryRequest(string MapId, int MaximumCount);
    private sealed record MapVersionRequest(string MapId, string VersionId);
}
