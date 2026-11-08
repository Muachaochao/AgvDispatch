using System.Text.Json;

namespace AgvDispatch.Application.Abstractions;

public static class TrafficControlOperations
{
    public const string ReviewMapActivation="ReviewMapActivation";
    public const string ListMapTransfers="ListMapTransfers";
    public const string GetMapTransferHistory="GetMapTransferHistory";
    public const string GetMapTransferReview="GetMapTransferReview";
    public const string PrepareMapTransfer="PrepareMapTransfer";
    public const string GetMapTransfer="GetMapTransfer";
    public const string ObserveMapTransfer="ObserveMapTransfer";
    public const string GetRobokitInspectionHistory = "GetRobokitInspectionHistory";
    public const string GetRobokitInspection = "GetRobokitInspection";
    public const string GetTrafficGeometryPreview="GetTrafficGeometryPreview";
    public const string GetTrafficBlocking="GetTrafficBlocking";
    public const string GetWaitingBayDeparture="GetWaitingBayDeparture";
    public const string GetTrafficWaiting = "GetTrafficWaiting";
    public const string GetTrafficMap = "GetTrafficMap";
    public const string GetTrafficLifecycle = "GetTrafficLifecycle";
    public const string GetSession = "GetSession";
    public const string GetCommunicationSettings = "GetCommunicationSettings";
    public const string SaveCommunicationSettings = "SaveCommunicationSettings";
    public const string ProbeConnection = "ProbeConnection";
    public const string GetTasks = "GetTasks";
    public const string GetTaskRoutePreview = "GetTaskRoutePreview";
    public const string GetTaskCreationOptions = "GetTaskCreationOptions";
    public const string CreateTask = "CreateTask";
    public const string GetTaskParameters = "GetTaskParameters";
    public const string ValidateTaskParameters = "ValidateTaskParameters";
    public const string SaveTaskParameters = "SaveTaskParameters";
    public const string SaveEngineeringTaskParameters = "SaveEngineeringTaskParameters";
    public const string GetTaskDetail = "GetTaskDetail";
    public const string PauseDispatchTask = "PauseDispatchTask";
    public const string ResumeDispatchTask = "ResumeDispatchTask";
    public const string GetTaskSwitchOptions = "GetTaskSwitchOptions";
    public const string GetTaskSwitch = "GetTaskSwitch";
    public const string CreateTaskSwitch = "CreateTaskSwitch";
    public const string SuspendTaskSwitch = "SuspendTaskSwitch";
    public const string ResumeTaskSwitch = "ResumeTaskSwitch";
    public const string AbandonTaskSwitch = "AbandonTaskSwitch";
    public const string GetTaskHistory = "GetTaskHistory";
    public const string GetTaskHistoryExportProgress = "GetTaskHistoryExportProgress";
    public const string CancelTaskHistoryExport = "CancelTaskHistoryExport";
    public const string GetTaskHistoryExportCache = "GetTaskHistoryExportCache";
    public const string CleanupTaskHistoryExportCache = "CleanupTaskHistoryExportCache";
    public const string ExportTaskHistory = "ExportTaskHistory";
    public const string GetSystemStatus = "GetSystemStatus";
    public const string GetDeviceMonitoring = "GetDeviceMonitoring";
    public const string GetAgvTrack = "GetAgvTrack";
    public const string GetAlarms = "GetAlarms";
    public const string GetAlarmPage = "GetAlarmPage";
    public const string AcknowledgeAlarm = "AcknowledgeAlarm";
    public const string GetMaps = "GetMaps";
    public const string GetMapWorkspace = "GetMapWorkspace";
    public const string CreateMap = "CreateMap";
    public const string SaveMapDraft = "SaveMapDraft";
    public const string PublishMap = "PublishMap";
    public const string GenerateMapUnderlay = "GenerateMapUnderlay";
    public const string SaveMapCalibration = "SaveMapCalibration";
    public const string ActivateMap = "ActivateMap";
    public const string GetMapSwitch = "GetMapSwitch";
    public const string BeginMapSwitch = "BeginMapSwitch";
    public const string ContinueMapDrain = "ContinueMapDrain";
    public const string ResumeMapSwitch = "ResumeMapSwitch";
    public const string CancelMapSwitch = "CancelMapSwitch";
    public const string PreviewMapImport = "PreviewMapImport";
    public const string ExportMap = "ExportMap";
    public const string GetMapAudits = "GetMapAudits";
    public const string GetMapVersionHistory = "GetMapVersionHistory";
    public const string GetMapVersion = "GetMapVersion";
    public const string GetResources = "GetResources";
    public const string GetTrafficFlow = "GetTrafficFlow";
    public const string GetAudits = "GetAudits";
    public const string GetReleaseRequests = "GetReleaseRequests";
    public const string SubmitRelease = "SubmitRelease";
    public const string ReviewRelease = "ReviewRelease";
    public const string GetTransportTimeoutRecovery = "GetTransportTimeoutRecovery";
    public const string ExecuteTransportTimeoutRecovery = "ExecuteTransportTimeoutRecovery";
}

public sealed record TrafficControlPipeRequest(string Operation, JsonElement Payload);

public sealed record TrafficControlPipeResponse(
    bool Success,
    string? Error,
    JsonElement Payload)
{
    public string? ErrorCode { get; init; }
}
