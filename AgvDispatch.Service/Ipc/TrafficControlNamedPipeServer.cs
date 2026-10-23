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
    IMapExecutionGate executionGate,
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
    ITrafficWaitingBayDepartureService? waitingBayDeparture=null, ISingleNavigationTestService? navigationTest=null) : IsolatedBackgroundService
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
        if(mapTransfers is not null)await mapTransfers.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    private async Task<CommunicationSettingsSnapshot> SaveCommunicationSettingsGuardedAsync(
        TrafficUserContext user, SaveCommunicationSettingsRequest request, CancellationToken cancellationToken)
    {
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.NewWork, cancellationToken);
        if (execution is null) throw new InvalidOperationException("地图切换期间不能修改通信配置。");
        return await communicationSettings.SaveAsync(user, request, cancellationToken);
    }

    // Map drafts can contain hundreds of stations and thousands of edges.
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("交通控制Named Pipe已启动。Pipe={PipeName}", options.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                pipeDiagnostics.ConnectionOpened();
                try
                {
                    await HandleConnectionAsync(pipe, stoppingToken);
                }
                finally
                {
                    pipeDiagnostics.ConnectionClosed();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "交通控制Named Pipe连接处理失败：{Error}", exception.Message);
            }
        }
    }

    private NamedPipeServerStream CreateServerPipe()
    {
        var security = new PipeSecurity();
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
        var identityName = "(unresolved)";
        string? operation = null;
        TrafficControlPipeResponse response;
        try
        {
            var request = await ReadAsync<TrafficControlPipeRequest>(pipe, cancellationToken);
            operation = request.Operation;
            // Windows only exposes the client token after at least one read from the pipe.
            identityName = pipe.GetImpersonationUserName();
            // No software login or role separation. Keep the OS identity only for operation auditing.
            var user = TrafficUserContext.LocalOperation(identityName);
            response = await DispatchAsync(user, request, cancellationToken);
            // Snapshot polling is not a business operation and must not fill the log.
            var logLevel = !response.Success ? LogLevel.Warning
                : request.Operation.StartsWith("Get", StringComparison.Ordinal) ? LogLevel.Debug
                : LogLevel.Information;
            logger.Log(logLevel,
                "交通控制请求完成。Identity={Identity}, Operation={Operation}, Success={Success}",
                identityName, request.Operation, response.Success);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "拒绝或处理交通控制请求失败。Identity={Identity}", identityName);
            response = Failure(exception is UnauthorizedAccessException
                ? exception.Message
                : $"后台处理失败：{exception.Message}");
            if (exception is UnauthorizedAccessException) response = response with { ErrorCode = "Unauthorized" };
        }
        pipeDiagnostics.RecordRequest(identityName, operation, response.Success);
        await WriteAsync(pipe, response, cancellationToken);
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

        return request.Operation switch
        {
            "GetNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).ReadAsync(cancellationToken)),
            "PreviewNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).PreviewAsync(user,Deserialize<NavigationTestQuery>(request.Payload),cancellationToken)),
            "SubmitNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).SubmitAsync(user,Deserialize<NavigationTestSubmit>(request.Payload),cancellationToken)),
            "CancelNavigationTest" => Success(await (navigationTest??throw new NotSupportedException("请更新单次导航后台。")).CancelAsync(user,Deserialize<NavigationTestCancel>(request.Payload),cancellationToken)),
            "GetCommissioningReport" => Success(await (commissioning??throw new NotSupportedException("当前后台未提供联调检查，请部署匹配版本。")).ReadAsync(user,Deserialize<CommissioningQuery>(request.Payload),cancellationToken)),
            "GetTaskRuntimeEvents" => Success(await (handshakes??throw new NotSupportedException("后台未配置运行事件查询。")).QueryAsync(Deserialize<TaskRuntimeQuery>(request.Payload),cancellationToken)),
            "ExportTaskDiagnostics" => Success(await (diagnosticExports??throw new NotSupportedException("后台未配置联调诊断导出。")).ExportAsync(Deserialize<TaskDetailRequest>(request.Payload).TaskId,cancellationToken)),
            "CancelDispatchTask" => Success(await taskCancellation.RequestAsync(user, Deserialize<TaskCancelRequest>(request.Payload), cancellationToken)),
            "BeginTaskRecoveryReview" => Success(await recovery.BeginReviewAsync(user, Deserialize<TaskRecoveryReviewRequest>(request.Payload), cancellationToken)),
            "GetExecutionRecovery" => Success(await recovery.GetAsync(cancellationToken)),
            "DecideExecutionRecovery" => Success(await recovery.DecideAsync(user, Deserialize<RecoveryDecisionRequest>(request.Payload), cancellationToken)),
            "ReleaseExecutionRecovery" => Success(await recovery.ReleaseAsync(user, Deserialize<RecoveryReleaseRequest>(request.Payload), cancellationToken)),
            TrafficControlOperations.GetCommunicationSettings => Success(await communicationSettings.GetAsync(user, cancellationToken)),
            TrafficControlOperations.SaveCommunicationSettings => Success(await SaveCommunicationSettingsGuardedAsync(user,
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
