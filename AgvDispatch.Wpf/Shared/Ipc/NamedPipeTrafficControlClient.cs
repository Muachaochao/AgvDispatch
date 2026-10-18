using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using System.Security.Principal;

namespace AgvDispatch.Wpf.Services;

public sealed class NamedPipeTrafficControlClient : ITrafficControlClient, IDeviceMonitoringClient, ICadDetailClient, IDisposable
{
    public Task<MapTransferPage> ListMapTransfersAsync(MapTransferListQuery query,CancellationToken ct)=>SendAsync<MapTransferPage>(TrafficControlOperations.ListMapTransfers,query,ct);
    public Task<MapTransferHistoryPage> GetMapTransferHistoryAsync(MapTransferHistoryQuery query,CancellationToken ct)=>SendAsync<MapTransferHistoryPage>(TrafficControlOperations.GetMapTransferHistory,query,ct);
    public Task<MapTransferReviewPlan> GetMapTransferReviewAsync(MapTransferQuery query,CancellationToken ct)=>SendAsync<MapTransferReviewPlan>(TrafficControlOperations.GetMapTransferReview,query,ct);
    public Task<MapTransferSnapshot> PrepareMapTransferAsync(PrepareMapTransferRequest request,CancellationToken ct)=>SendAsync<MapTransferSnapshot>(TrafficControlOperations.PrepareMapTransfer,request,ct);
    public Task<MapTransferSnapshot> GetMapTransferAsync(MapTransferQuery query,CancellationToken ct)=>SendAsync<MapTransferSnapshot>(TrafficControlOperations.GetMapTransfer,query,ct);
    public Task<MapTransferSnapshot> ObserveMapTransferAsync(MapTransferQuery query,CancellationToken ct)=>SendAsync<MapTransferSnapshot>(TrafficControlOperations.ObserveMapTransfer,query,ct);
    public Task<IReadOnlyList<RobokitInspectionRecord>> GetRobokitInspectionHistoryAsync(RobokitInspectionHistoryQuery query,CancellationToken ct) => SendListAsync<RobokitInspectionRecord>(TrafficControlOperations.GetRobokitInspectionHistory,query,ct);
    public Task<RobokitInspectionSnapshot> GetRobokitInspectionAsync(RobokitInspectionQuery query, CancellationToken ct) => SendAsync<RobokitInspectionSnapshot>(TrafficControlOperations.GetRobokitInspection, query, ct);
    public Task<TrafficGeometryPreviewSnapshot> GetTrafficGeometryPreviewAsync(TrafficGeometryPreviewQuery query,CancellationToken ct)=>SendAsync<TrafficGeometryPreviewSnapshot>(TrafficControlOperations.GetTrafficGeometryPreview,query,ct);
    public Task<TaskCancelResult> CancelDispatchTaskAsync(TaskCancelRequest request, CancellationToken ct) => SendAsync<TaskCancelResult>("CancelDispatchTask", request, ct);
    public Task<RecoveryResult> BeginTaskRecoveryReviewAsync(TaskRecoveryReviewRequest request, CancellationToken ct) => SendAsync<RecoveryResult>("BeginTaskRecoveryReview", request, ct);
    public Task<ManualTaskRoutePreview> GetTaskRoutePreviewAsync(ManualTaskRoutePreviewRequest request,CancellationToken ct)=>SendAsync<ManualTaskRoutePreview>(TrafficControlOperations.GetTaskRoutePreview,request,ct);
    public Task<ManualTaskCreationOptions> GetTaskCreationOptionsAsync(CancellationToken ct) =>
        SendAsync<ManualTaskCreationOptions>(TrafficControlOperations.GetTaskCreationOptions, new { }, ct);
    public Task<TaskParameterSnapshot> GetTaskParametersAsync(CancellationToken ct) => SendAsync<TaskParameterSnapshot>(TrafficControlOperations.GetTaskParameters, new { }, ct);
    public Task<TaskParameterResult> ValidateTaskParametersAsync(TaskParameterRequest request, CancellationToken ct) => SendAsync<TaskParameterResult>(TrafficControlOperations.ValidateTaskParameters, request, ct);
    public Task<TaskParameterResult> SaveTaskParametersAsync(TaskParameterRequest request, CancellationToken ct) => SendAsync<TaskParameterResult>(TrafficControlOperations.SaveTaskParameters, request, ct);
    public Task<ManualTaskCreationResult> CreateTaskAsync(ManualTaskCreationRequest request, CancellationToken ct) =>
        SendAsync<ManualTaskCreationResult>(TrafficControlOperations.CreateTask, request, ct);
    // Keep this aligned with the Service limit so complete map drafts remain one atomic request.
    private const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _pipeName;
    private readonly object _lifetimeLock = new();
    private bool _disposed;

    public void Dispose()
    {
        lock (_lifetimeLock) { _disposed = true; }
    }

    public NamedPipeTrafficControlClient(string pipeName)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Named Pipe名称不能为空。", nameof(pipeName))
            : pipeName;
    }

    public Task<TrafficControlSession> GetSessionAsync(CancellationToken cancellationToken) =>
        SendAsync<TrafficControlSession>(TrafficControlOperations.GetSession, new { }, cancellationToken);

    public Task<CadDetailResult> GetCadDetailAsync(CadDetailRequest request, CancellationToken cancellationToken) =>
        SendAsync<CadDetailResult>("GetCadDetail", request, cancellationToken, ".cad-detail");

    public Task<DeviceMonitoringSnapshot> GetDeviceMonitoringAsync(CancellationToken cancellationToken) =>
        SendAsync<DeviceMonitoringSnapshot>(TrafficControlOperations.GetDeviceMonitoring, new { }, cancellationToken);

    public Task<RecoveryStatus> GetExecutionRecoveryAsync(CancellationToken ct) => SendAsync<RecoveryStatus>("GetExecutionRecovery", new { }, ct);
    public Task<RecoveryResult> DecideExecutionRecoveryAsync(RecoveryDecisionRequest request, CancellationToken ct) => SendAsync<RecoveryResult>("DecideExecutionRecovery", request, ct);
    public Task<RecoveryResult> ReleaseExecutionRecoveryAsync(RecoveryReleaseRequest request, CancellationToken ct) => SendAsync<RecoveryResult>("ReleaseExecutionRecovery", request, ct);

    public Task<CommunicationSettingsSnapshot> GetCommunicationSettingsAsync(CancellationToken cancellationToken) =>
        SendAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, cancellationToken);
    public Task<CommunicationSettingsSnapshot> SaveCommunicationSettingsAsync(SaveCommunicationSettingsRequest request, CancellationToken cancellationToken) =>
        SendAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.SaveCommunicationSettings, request, cancellationToken);
    public Task<ConnectionProbeResult> ProbeConnectionAsync(ConnectionProbeRequest request, CancellationToken cancellationToken) =>
        SendAsync<ConnectionProbeResult>(TrafficControlOperations.ProbeConnection, request, cancellationToken);

    public Task<TaskListPage> GetTasksAsync(TaskListQuery query, CancellationToken cancellationToken) =>
        SendAsync<TaskListPage>(TrafficControlOperations.GetTasks, query, cancellationToken);

    public Task<TaskHistoryExportProgress> GetTaskHistoryExportProgressAsync(TaskHistoryExportJobRequest request, CancellationToken ct) => SendAsync<TaskHistoryExportProgress>(TrafficControlOperations.GetTaskHistoryExportProgress, request, ct);
    public Task<TaskHistoryExportCancelResult> CancelTaskHistoryExportAsync(TaskHistoryExportJobRequest request, CancellationToken ct) => SendAsync<TaskHistoryExportCancelResult>(TrafficControlOperations.CancelTaskHistoryExport, request, ct);
    public Task<TaskHistoryExportCache> GetTaskHistoryExportCacheAsync(TaskHistoryExportCacheRequest request, CancellationToken ct) => SendAsync<TaskHistoryExportCache>(TrafficControlOperations.GetTaskHistoryExportCache, request, ct);
    public Task<TaskHistoryExportCleanupResult> CleanupTaskHistoryExportCacheAsync(TaskHistoryExportCacheRequest request, CancellationToken ct) => SendAsync<TaskHistoryExportCleanupResult>(TrafficControlOperations.CleanupTaskHistoryExportCache, request, ct);
    public Task<TaskHistoryExportResult> ExportTaskHistoryAsync(TaskHistoryExportRequest request, CancellationToken ct) =>
        SendAsync<TaskHistoryExportResult>(TrafficControlOperations.ExportTaskHistory, request, ct);

    public Task<TaskHistoryPage> GetTaskHistoryAsync(TaskHistoryQuery query, CancellationToken cancellationToken) =>
        SendAsync<TaskHistoryPage>(TrafficControlOperations.GetTaskHistory, query, cancellationToken);

    public Task<TaskCenterDetail?> GetTaskDetailAsync(string taskId, CancellationToken cancellationToken) =>
        SendNullableAsync<TaskCenterDetail>(TrafficControlOperations.GetTaskDetail,
            new TaskDetailRequest(taskId, IncludeOperationHistory: false), cancellationToken);

    public Task<SystemStatusSnapshot> GetSystemStatusAsync(CancellationToken cancellationToken) =>
        SendAsync<SystemStatusSnapshot>(TrafficControlOperations.GetSystemStatus, new { }, cancellationToken);

    public Task<IReadOnlyList<AgvTrackPointSnapshot>> GetAgvTrackAsync(
        AgvTrackQueryRequest request,
        CancellationToken cancellationToken) =>
        SendListAsync<AgvTrackPointSnapshot>(
            TrafficControlOperations.GetAgvTrack, request, cancellationToken);

    public Task<IReadOnlyList<AlarmSnapshot>> GetAlarmsAsync(
        int maximumCount,
        bool activeOnly,
        CancellationToken cancellationToken) =>
        SendListAsync<AlarmSnapshot>(
            TrafficControlOperations.GetAlarms,
            new { MaximumCount = maximumCount, ActiveOnly = activeOnly },
            cancellationToken);

    public Task<AlarmOperationResult> AcknowledgeAlarmAsync(
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<AlarmOperationResult>(
            TrafficControlOperations.AcknowledgeAlarm, request, cancellationToken);

    public Task<IReadOnlyList<MapProjectSummary>> GetMapsAsync(CancellationToken cancellationToken) =>
        SendListAsync<MapProjectSummary>(TrafficControlOperations.GetMaps, new { }, cancellationToken);

    public Task<MapWorkspaceSnapshot?> GetMapWorkspaceAsync(
        string mapId,
        CancellationToken cancellationToken) =>
        SendNullableAsync<MapWorkspaceSnapshot>(
            TrafficControlOperations.GetMapWorkspace, new { MapId = mapId }, cancellationToken);

    public Task<MapOperationResult> CreateMapAsync(
        CreateMapRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapOperationResult>(TrafficControlOperations.CreateMap, request, cancellationToken);

    public Task<MapOperationResult> SaveMapDraftAsync(
        SaveMapDraftRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapOperationResult>(TrafficControlOperations.SaveMapDraft, request, cancellationToken);

    public Task<MapOperationResult> PublishMapAsync(
        PublishMapRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapOperationResult>(TrafficControlOperations.PublishMap, request, cancellationToken);

    public Task<MapOperationResult> GenerateMapUnderlayAsync(
        GenerateMapUnderlayRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapOperationResult>(TrafficControlOperations.GenerateMapUnderlay, request, cancellationToken);

    public Task<MapOperationResult> SaveMapCalibrationAsync(
        SaveMapCalibrationRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapOperationResult>(TrafficControlOperations.SaveMapCalibration, request, cancellationToken);

    public Task<MapOperationResult> ActivateMapAsync(
        ActivateMapRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapOperationResult>(TrafficControlOperations.ActivateMap, request, cancellationToken);

    public Task<MapActivationReview> ReviewMapActivationAsync(MapActivationReviewQuery query,CancellationToken cancellationToken) => SendAsync<MapActivationReview>(TrafficControlOperations.ReviewMapActivation,query,cancellationToken);
    public Task<MapSwitchStatus> GetMapSwitchAsync(CancellationToken cancellationToken) =>
        SendAsync<MapSwitchStatus>(TrafficControlOperations.GetMapSwitch, new { }, cancellationToken);
    public Task<MapSwitchResult> BeginMapSwitchAsync(BeginMapSwitchRequest request, CancellationToken cancellationToken) =>
        SendAsync<MapSwitchResult>(TrafficControlOperations.BeginMapSwitch, request, cancellationToken);
    public Task<MapSwitchResult> ContinueMapDrainAsync(FinishMapSwitchRequest request, CancellationToken cancellationToken) =>
        SendAsync<MapSwitchResult>(TrafficControlOperations.ContinueMapDrain, request, cancellationToken);
    public Task<MapSwitchResult> ResumeMapSwitchAsync(FinishMapSwitchRequest request, CancellationToken cancellationToken) =>
        SendAsync<MapSwitchResult>(TrafficControlOperations.ResumeMapSwitch, request, cancellationToken);
    public Task<MapSwitchResult> CancelMapSwitchAsync(FinishMapSwitchRequest request, CancellationToken cancellationToken) =>
        SendAsync<MapSwitchResult>(TrafficControlOperations.CancelMapSwitch, request, cancellationToken);

    public Task<MapImportPreviewResult> PreviewMapImportAsync(
        PreviewMapImportRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapImportPreviewResult>(TrafficControlOperations.PreviewMapImport, request, cancellationToken);

    public Task<MapExportResult> ExportMapAsync(
        ExportMapRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<MapExportResult>(TrafficControlOperations.ExportMap, request, cancellationToken);

    public Task<IReadOnlyList<MapOperationAuditSnapshot>> GetMapAuditsAsync(
        string mapId,
        int maximumCount,
        CancellationToken cancellationToken) =>
        SendListAsync<MapOperationAuditSnapshot>(
            TrafficControlOperations.GetMapAudits,
            new { MapId = mapId, MaximumCount = maximumCount },
            cancellationToken);

    public Task<IReadOnlyList<MapVersionHistoryItem>> GetMapVersionHistoryAsync(
        string mapId,
        CancellationToken cancellationToken) =>
        SendListAsync<MapVersionHistoryItem>(
            TrafficControlOperations.GetMapVersionHistory, new { MapId = mapId }, cancellationToken);

    public Task<MapVersionSnapshot?> GetMapVersionAsync(
        string mapId,
        string versionId,
        CancellationToken cancellationToken) =>
        SendNullableAsync<MapVersionSnapshot>(
            TrafficControlOperations.GetMapVersion, new { MapId = mapId, VersionId = versionId }, cancellationToken);

    public Task<TrafficBlockingDetails> GetTrafficBlockingAsync(TrafficBlockingQuery query,CancellationToken ct)=>
        SendAsync<TrafficBlockingDetails>(TrafficControlOperations.GetTrafficBlocking,query,ct);
    public Task<TrafficWaitingPage> GetTrafficWaitingAsync(TrafficWaitingQuery query, CancellationToken ct) =>
        SendAsync<TrafficWaitingPage>(TrafficControlOperations.GetTrafficWaiting, query, ct);

    public Task<TrafficMapSnapshot> GetTrafficMapAsync(TrafficMapQuery query, CancellationToken ct) =>
        SendAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap, query, ct);

    public Task<TrafficLifecycleSnapshot> GetTrafficLifecycleAsync(CancellationToken ct) =>
        SendAsync<TrafficLifecycleSnapshot>(TrafficControlOperations.GetTrafficLifecycle, new { }, ct);

    public Task<IReadOnlyList<TrafficResourceSnapshot>> GetResourcesAsync(
        CancellationToken cancellationToken) =>
        SendListAsync<TrafficResourceSnapshot>(TrafficControlOperations.GetResources, new { }, cancellationToken);

    public Task<TrafficFlowSnapshot> GetTrafficFlowAsync(CancellationToken cancellationToken) =>
        SendAsync<TrafficFlowSnapshot>(TrafficControlOperations.GetTrafficFlow, new { }, cancellationToken);

    public Task<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>> GetRecentAuditsAsync(
        int maximumCount,
        CancellationToken cancellationToken) =>
        SendListAsync<TrafficResourceReleaseAuditSnapshot>(
            TrafficControlOperations.GetAudits, new { MaximumCount = maximumCount }, cancellationToken);

    public Task<IReadOnlyList<ManualReleaseWorkflowItem>> GetReleaseRequestsAsync(
        int maximumCount,
        CancellationToken cancellationToken) =>
        SendListAsync<ManualReleaseWorkflowItem>(
            TrafficControlOperations.GetReleaseRequests,
            new { MaximumCount = maximumCount }, cancellationToken);

    public Task<ManualReleaseWorkflowResult> SubmitReleaseAsync(
        SubmitManualReleaseRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ManualReleaseWorkflowResult>(
            TrafficControlOperations.SubmitRelease, request, cancellationToken);

    public Task<ManualReleaseWorkflowResult> ReviewReleaseAsync(
        ReviewManualReleaseRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ManualReleaseWorkflowResult>(
            TrafficControlOperations.ReviewRelease, request, cancellationToken);

    private async Task<IReadOnlyList<T>> SendListAsync<T>(
        string operation,
        object payload,
        CancellationToken cancellationToken) =>
        await SendAsync<T[]>(operation, payload, cancellationToken);

    private async Task<T?> SendNullableAsync<T>(
        string operation,
        object payload,
        CancellationToken cancellationToken) where T : class
    {
        var element = await SendAsync<JsonElement>(operation, payload, cancellationToken);
        return element.ValueKind == JsonValueKind.Null
            ? null
            : element.Deserialize<T>(JsonOptions);
    }

    private async Task<T> SendAsync<T>(
        string operation,
        object payload,
        CancellationToken cancellationToken,
        string pipeSuffix = "")
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        return await SendCoreAsync<T>(operation, payload, cancellationToken, pipeSuffix);
    }

    private async Task<T> SendCoreAsync<T>(string operation, object payload, CancellationToken cancellationToken, string pipeSuffix)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", _pipeName + pipeSuffix, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            TokenImpersonationLevel.Identification);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"无法在3秒内连接后台服务Named Pipe：{_pipeName}。");
        }

        var request = new TrafficControlPipeRequest(
            operation, JsonSerializer.SerializeToElement(payload, JsonOptions));
        await WriteAsync(pipe, request, cancellationToken);
        var response = await ReadAsync<TrafficControlPipeResponse>(pipe, cancellationToken);
        if (!response.Success)
        {
            if (response.ErrorCode == "Unauthorized") throw new UnauthorizedAccessException(response.Error ?? "后台拒绝了操作。");
            throw new InvalidOperationException(response.Error ?? "后台拒绝了请求。");
        }
        return response.Payload.Deserialize<T>(JsonOptions) ??
               throw new InvalidDataException($"后台响应{typeof(T).Name}为空。");
    }

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
               throw new InvalidDataException("Named Pipe响应为空。");
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
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
}
