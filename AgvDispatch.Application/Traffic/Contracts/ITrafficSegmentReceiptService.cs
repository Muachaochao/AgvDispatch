namespace AgvDispatch.Application.Abstractions;

public enum TrafficSegmentReceiptState { Unavailable, Pending, Expired, PreviousSession, Invalidated, Committed }
public sealed record TrafficSegmentReceiptView(string? ReceiptId,TrafficSegmentReceiptState State,string Reason,
    DateTimeOffset? ExpiresAt,int CandidateCount)
{
    public bool ResourcesReleased=>false;
    public IReadOnlyList<TrafficSegmentReleaseCandidate> Candidates { get; init; } = [];
    public TrafficSegmentProgressSummary? Progress { get; init; }
}

public sealed class TrafficSegmentCommitOptions
{
    public bool Enabled { get; set; }
    public int ReceiptValiditySeconds { get; set; } = 2;

    public void Validate()
    {
        if (ReceiptValiditySeconds is < 2 or > 30)
            throw new InvalidOperationException(
                "TrafficSegmentRelease:ReceiptValiditySeconds 必须在2～30秒之间。");
    }
}
public sealed record TrafficSegmentCommitRequest(string ReceiptId,string RequestId);
public sealed record TrafficSegmentCommitResult(bool Accepted,bool Replayed,string Reason,IReadOnlyList<string> ReleasedLeaseIds)
{
    public bool ResourcesReleasedNow=>Accepted&&!Replayed&&ReleasedLeaseIds.Count>0;
}
public interface ITrafficSegmentCommitService
{
    Task<TrafficSegmentCommitResult> CommitAsync(TrafficSegmentCommitRequest request,CancellationToken ct);
    Task<TrafficSegmentCommitResult?> ReadCommittedAsync(TrafficSegmentCommitRequest request,CancellationToken ct);
}

// Only opaque identifiers; no caller-supplied ownership, evidence or candidate unlock list.
public interface ITrafficSegmentReceiptService
{
    Task<TrafficSegmentReceiptView> IssueAsync(string stepId,CancellationToken ct);
    Task<TrafficSegmentReceiptView> ReadAsync(string receiptId,CancellationToken ct);
}
