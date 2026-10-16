namespace AgvDispatch.Application.Abstractions;

public sealed record PrepareMapTransferRequest(string RequestId,string MapId,string VersionId,string VehicleId,
    string ExpectedDeviceMd5,string ConfirmedRobokitVersion,string ConfirmationNote,bool Load);
public sealed record MapTransferQuery(string OperationId);
public sealed record MapTransferEventSnapshot(string Kind,string Operator,string Evidence,DateTimeOffset OccurredAt);
public sealed record MapTransferSnapshot(string OperationId,string RequestId,string MapId,string VersionId,string VehicleId,
    string TargetMapName,string SourceSha256,string ExpectedDeviceMd5,string State,int AttemptCount,
    bool PreviousSession,bool GatesClear,IReadOnlyList<string> Blockers,string LastObservation,
    DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt)
{
    public bool WritesEnabled=>false;
    public IReadOnlyList<MapTransferEventSnapshot> Events{get;init;}=[];
}
public sealed record MapTransferListQuery(string? VehicleId=null,string? MapId=null,string? State=null,
    DateTimeOffset? From=null,DateTimeOffset? To=null,int Page=1,int PageSize=25);
public sealed record MapTransferRecord(string OperationId,string RequestId,string MapId,string VersionId,string VehicleId,
    string TargetMapName,string State,int AttemptCount,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
public sealed record MapTransferPage(IReadOnlyList<MapTransferRecord> Items,int Total,int Page,int PageSize);
public sealed record MapTransferHistoryQuery(string OperationId,int Page=1,int PageSize=25);
public sealed record MapTransferHistoryPage(string OperationId,IReadOnlyList<MapTransferEventSnapshot> Items,int Total,int Page,int PageSize);
/// <summary>Read-only disposition contract. No acknowledgement, closure, resend or gate-release authority.</summary>
public sealed record MapTransferReviewPlan(MapTransferSnapshot Record,bool RequiresDisposition,
    IReadOnlyList<string> RequiredEvidence,IReadOnlyList<string> ProhibitedActions)
{
    public bool CanClose=>false;
    public bool CanResend=>false;
    public bool CanRelease=>false;
    public bool ObservationProvesThisWrite=>false;
}
public interface IMapTransferLedger
{
    Task<MapTransferPage> ListAsync(TrafficUserContext user,MapTransferListQuery query,CancellationToken ct);
    Task<MapTransferHistoryPage> HistoryAsync(TrafficUserContext user,MapTransferHistoryQuery query,CancellationToken ct);
    Task<MapTransferReviewPlan> ReviewAsync(TrafficUserContext user,MapTransferQuery query,CancellationToken ct);
    Task InitializeAsync(CancellationToken ct);
    Task<MapTransferSnapshot> PrepareAsync(TrafficUserContext user,PrepareMapTransferRequest request,CancellationToken ct);
    Task<MapTransferSnapshot> ReadAsync(TrafficUserContext user,MapTransferQuery query,CancellationToken ct);
    Task<MapTransferSnapshot> ObserveAsync(TrafficUserContext user,MapTransferQuery query,CancellationToken ct);
}
public interface IMapTransferRecoveryBarrier
{
    bool IsOpen{get;}
}
