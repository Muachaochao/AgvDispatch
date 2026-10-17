namespace AgvDispatch.Application.Abstractions;

public interface IRecoveryBarrier { bool IsOpen { get; } }

public enum RecoveryDecision { ContinueOriginal = 10, RecordPhysicalCompletion = 20, CloseCancelled = 30 }
public sealed record RecoveryItem(string TicketId, string TaskId, string StepId, string StationId,
    string? VehicleId, string Step, string State, string Reason, string Evidence, bool Verified,
    string? MaterialReference, int Revision)
{
    public string VehicleSummary { get; init; } = "车辆状态等待采集。";
    public string PlcSummary { get; init; } = "PLC状态等待采集。";
    public string ResourceSummary { get; init; } = "路权状态等待采集。";
    public string IdentitySummary { get; init; } = "无法自动核验：设备身份字段尚未接入，保留现场核对。";
    public string IdentityState { get; init; } = "Unavailable";
    public bool IdentityBlocksRecovery { get; init; }
    public string MachineContainerSummary { get; init; } = "设备容器身份未接入。";
    public string LoadSummary => MachineContainerSummary.TrimEnd('。') + "；" + (MaterialReference == null ? "尚未提交本次现场核对说明，须核对载荷、箱体位置与原任务归属。" : "人工核对载荷：" + MaterialReference);
    public int? TaskStage { get; init; }
    public string TaskText => TaskId == "__SITE__" ? "全场核对" : TaskId;
    public string StepText => Step switch
    {
        "GoToPickup" => "前往取料站", "PickupFullBox" => "取满箱握手", "DeliverFullBoxToDumper" => "满箱运输",
        "WaitForDumperReady" => "等待倒料机", "EnterDumpingPosition" => "进入倒料位", "DumpFullBox" => "翻料握手",
        "ReturnEmptyBox" => "空箱返回", "PlaceEmptyBox" => "放空箱握手", "CompletePlcHandshake" => "末端握手清理", _ => Step
    };
}
public sealed record RecoveryAudit(string Id, string SessionId, string TaskId, string StepId,
    string Operator, string Decision, string Reason, string Evidence, DateTime OccurredAt)
{
    public string DecisionText => Decision switch
    {
        "CloseCancelled" => "核对后关闭取消", "RuntimeReview" => "运行中现场核对", "BootHold" => "启动冻结", "Release" => "恢复放行", "ContinueOriginal" => "原节点续接", "RecordPhysicalCompletion" => "到站补记",
        "VerifiedUnstarted" => "未开始请求已核对", "VerificationRevoked" => "撤销过期确认", "RecoveryWaiting" => "等待现场核对", "RecoveryRejected" => "拒绝恢复请求", _ => Decision
    };
}
public sealed record RecoveryStatus(string SessionId, bool Released, string Message,
    IReadOnlyList<RecoveryItem> Items, IReadOnlyList<RecoveryAudit> Audits);
public sealed record RecoveryDecisionRequest(string SessionId, string TicketId, int Revision,
    RecoveryDecision Decision, string MaterialReference, string Reason,
    bool VehicleAndLocationConfirmed, bool LoadAndOwnershipConfirmed, bool PlcCycleConfirmed)
{
    public bool HandshakePendingWriteConfirmed { get; init; }
}
public sealed record RecoveryReleaseRequest(string SessionId, string Reason);
public sealed record RecoveryResult(bool Accepted, string Message);

public interface IExecutionRecoveryService
{
    Task<RecoveryResult> BeginReviewAsync(TrafficUserContext user, TaskRecoveryReviewRequest request, CancellationToken ct) => throw new NotSupportedException("后台尚不支持运行中恢复核对。");
    Task InitializeAsync(CancellationToken ct);
    Task InspectAsync(CancellationToken ct);
    Task<RecoveryStatus> GetAsync(CancellationToken ct);
    Task<RecoveryResult> DecideAsync(TrafficUserContext user, RecoveryDecisionRequest request, CancellationToken ct);
    Task<RecoveryResult> ReleaseAsync(TrafficUserContext user, RecoveryReleaseRequest request, CancellationToken ct);
}

public interface IExecutionEvidenceJournal
{
    IDisposable BeginScope(string taskId, string stepId);
    Task RecordAsync(string taskId, string stepId, string kind, object evidence, CancellationToken ct);
    Task RecordCurrentAsync(string kind, object evidence, CancellationToken ct);
}
