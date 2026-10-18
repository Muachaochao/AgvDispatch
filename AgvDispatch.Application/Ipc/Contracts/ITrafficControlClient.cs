namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficControlSession(
    string WindowsIdentity,
    string DisplayName,
    TrafficPermission Permissions)
{
    public TrafficRole Role { get; init; } = TrafficRole.Custom;
}

public interface ITrafficControlClient
{
    Task<MapTransferPage> ListMapTransfersAsync(MapTransferListQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持地图传输历史与处置条件，请更新后台。");
    Task<MapTransferHistoryPage> GetMapTransferHistoryAsync(MapTransferHistoryQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持地图传输历史与处置条件，请更新后台。");
    Task<MapTransferReviewPlan> GetMapTransferReviewAsync(MapTransferQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持地图传输历史与处置条件，请更新后台。");
    Task<MapTransferSnapshot> PrepareMapTransferAsync(PrepareMapTransferRequest request,CancellationToken ct)=>throw new NotSupportedException("后台未支持地图传输记录。");
    Task<MapTransferSnapshot> GetMapTransferAsync(MapTransferQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持地图传输记录。");
    Task<MapTransferSnapshot> ObserveMapTransferAsync(MapTransferQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持地图传输复核。");
    Task<IReadOnlyList<RobokitInspectionRecord>> GetRobokitInspectionHistoryAsync(RobokitInspectionHistoryQuery query,CancellationToken ct) => throw new NotSupportedException("后台未支持核验记录，请更新后台。");
    Task<RobokitInspectionSnapshot> GetRobokitInspectionAsync(RobokitInspectionQuery query, CancellationToken ct) => throw new NotSupportedException("后台未支持Robokit只读核验，请更新后台。");
    Task<TrafficGeometryPreviewSnapshot> GetTrafficGeometryPreviewAsync(TrafficGeometryPreviewQuery query,CancellationToken ct) => throw new NotSupportedException("后台未支持几何只读预检，请更新后台。");
    Task<TrafficBlockingDetails> GetTrafficBlockingAsync(TrafficBlockingQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持阻塞链详情。");
    Task<TrafficWaitingPage> GetTrafficWaitingAsync(TrafficWaitingQuery query, CancellationToken ct) => throw new NotSupportedException("后台未支持等待队列。");
    Task<TrafficMapSnapshot> GetTrafficMapAsync(TrafficMapQuery query, CancellationToken ct) => throw new NotSupportedException("后台未支持交通地图联动。");
    Task<TrafficLifecycleSnapshot> GetTrafficLifecycleAsync(CancellationToken ct) => throw new NotSupportedException("后台尚不支持路权过程监控。");
    Task<TaskCancelResult> CancelDispatchTaskAsync(TaskCancelRequest request, CancellationToken ct) => throw new NotSupportedException("后台尚不支持任务取消。");
    Task<RecoveryResult> BeginTaskRecoveryReviewAsync(TaskRecoveryReviewRequest request, CancellationToken ct) => throw new NotSupportedException("后台尚不支持运行中恢复核对。");
    Task<TaskParameterSnapshot> GetTaskParametersAsync(CancellationToken ct) => throw new NotSupportedException("后台尚不支持任务参数编辑。");
    Task<TaskParameterResult> ValidateTaskParametersAsync(TaskParameterRequest request, CancellationToken ct) => throw new NotSupportedException("后台尚不支持任务参数校验。");
    Task<TaskParameterResult> SaveTaskParametersAsync(TaskParameterRequest request, CancellationToken ct) => throw new NotSupportedException("后台尚不支持任务参数保存。");
    Task<ManualTaskRoutePreview> GetTaskRoutePreviewAsync(ManualTaskRoutePreviewRequest request,CancellationToken ct)=>throw new NotSupportedException("后台未支持当前任务路线预览，请更新后台。");
    Task<ManualTaskCreationOptions> GetTaskCreationOptionsAsync(CancellationToken ct) => throw new NotSupportedException("后台尚不支持手动创建任务。");
    Task<ManualTaskCreationResult> CreateTaskAsync(ManualTaskCreationRequest request, CancellationToken ct) => throw new NotSupportedException("后台尚不支持手动创建任务。");
    Task<RecoveryStatus> GetExecutionRecoveryAsync(CancellationToken ct) => throw new NotSupportedException("服务尚不支持重启恢复。");
    Task<RecoveryResult> DecideExecutionRecoveryAsync(RecoveryDecisionRequest request, CancellationToken ct) => throw new NotSupportedException("服务尚不支持重启恢复。");
    Task<RecoveryResult> ReleaseExecutionRecoveryAsync(RecoveryReleaseRequest request, CancellationToken ct) => throw new NotSupportedException("服务尚不支持重启恢复。");
    Task<CommunicationSettingsSnapshot> GetCommunicationSettingsAsync(CancellationToken cancellationToken);
    Task<CommunicationSettingsSnapshot> SaveCommunicationSettingsAsync(SaveCommunicationSettingsRequest request, CancellationToken cancellationToken);
    Task<ConnectionProbeResult> ProbeConnectionAsync(ConnectionProbeRequest request, CancellationToken cancellationToken);
    Task<TaskListPage> GetTasksAsync(TaskListQuery query, CancellationToken cancellationToken);
    Task<TaskCenterDetail?> GetTaskDetailAsync(string taskId, CancellationToken cancellationToken);
    Task<TaskHistoryExportProgress> GetTaskHistoryExportProgressAsync(TaskHistoryExportJobRequest request, CancellationToken ct) => throw new NotSupportedException("后台不支持导出进度，请更新服务。");
    Task<TaskHistoryExportCancelResult> CancelTaskHistoryExportAsync(TaskHistoryExportJobRequest request, CancellationToken ct) => throw new NotSupportedException("后台不支持取消导出，请更新服务。");
    Task<TaskHistoryExportCache> GetTaskHistoryExportCacheAsync(TaskHistoryExportCacheRequest request, CancellationToken ct) => throw new NotSupportedException("后台不支持缓存管理，请更新服务。");
    Task<TaskHistoryExportCleanupResult> CleanupTaskHistoryExportCacheAsync(TaskHistoryExportCacheRequest request, CancellationToken ct) => throw new NotSupportedException("后台不支持缓存管理，请更新服务。");
    Task<TaskHistoryExportResult> ExportTaskHistoryAsync(TaskHistoryExportRequest request, CancellationToken ct) => throw new NotSupportedException("后台不支持历史导出，请更新服务。");
    Task<TaskHistoryPage> GetTaskHistoryAsync(TaskHistoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException("后台不支持历史分页，请更新服务。");
    Task<TrafficControlSession> GetSessionAsync(CancellationToken cancellationToken);
    Task<SystemStatusSnapshot> GetSystemStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AgvTrackPointSnapshot>> GetAgvTrackAsync(
        AgvTrackQueryRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AlarmSnapshot>> GetAlarmsAsync(
        int maximumCount,
        bool activeOnly,
        CancellationToken cancellationToken);
    Task<AlarmOperationResult> AcknowledgeAlarmAsync(
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MapProjectSummary>> GetMapsAsync(CancellationToken cancellationToken);
    Task<MapWorkspaceSnapshot?> GetMapWorkspaceAsync(
        string mapId,
        CancellationToken cancellationToken);
    Task<MapOperationResult> CreateMapAsync(
        CreateMapRequest request,
        CancellationToken cancellationToken);
    Task<MapOperationResult> SaveMapDraftAsync(
        SaveMapDraftRequest request,
        CancellationToken cancellationToken);
    Task<MapOperationResult> PublishMapAsync(
        PublishMapRequest request,
        CancellationToken cancellationToken);
    Task<MapOperationResult> GenerateMapUnderlayAsync(
        GenerateMapUnderlayRequest request,
        CancellationToken cancellationToken);
    Task<MapOperationResult> SaveMapCalibrationAsync(
        SaveMapCalibrationRequest request,
        CancellationToken cancellationToken);
    Task<MapOperationResult> ActivateMapAsync(
        ActivateMapRequest request,
        CancellationToken cancellationToken);
    Task<MapActivationReview> ReviewMapActivationAsync(MapActivationReviewQuery query, CancellationToken cancellationToken) => throw new NotSupportedException("后台未提供地图激活只读复核。");
    Task<MapSwitchStatus> GetMapSwitchAsync(CancellationToken cancellationToken) => throw new NotSupportedException("服务不支持安全切换。");
    Task<MapSwitchResult> BeginMapSwitchAsync(BeginMapSwitchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("服务不支持安全切换。");
    Task<MapSwitchResult> ContinueMapDrainAsync(FinishMapSwitchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("服务不支持安全切换。");
    Task<MapSwitchResult> ResumeMapSwitchAsync(FinishMapSwitchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("服务不支持安全切换。");
    Task<MapSwitchResult> CancelMapSwitchAsync(FinishMapSwitchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("服务不支持安全切换。");
    Task<MapImportPreviewResult> PreviewMapImportAsync(
        PreviewMapImportRequest request,
        CancellationToken cancellationToken);
    Task<MapExportResult> ExportMapAsync(
        ExportMapRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MapOperationAuditSnapshot>> GetMapAuditsAsync(
        string mapId,
        int maximumCount,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MapVersionHistoryItem>> GetMapVersionHistoryAsync(
        string mapId,
        CancellationToken cancellationToken);
    Task<MapVersionSnapshot?> GetMapVersionAsync(
        string mapId,
        string versionId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<TrafficResourceSnapshot>> GetResourcesAsync(CancellationToken cancellationToken);
    Task<TrafficFlowSnapshot> GetTrafficFlowAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>> GetRecentAuditsAsync(
        int maximumCount,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ManualReleaseWorkflowItem>> GetReleaseRequestsAsync(
        int maximumCount,
        CancellationToken cancellationToken);
    Task<ManualReleaseWorkflowResult> SubmitReleaseAsync(
        SubmitManualReleaseRequest request,
        CancellationToken cancellationToken);
    Task<ManualReleaseWorkflowResult> ReviewReleaseAsync(
        ReviewManualReleaseRequest request,
        CancellationToken cancellationToken);
}
