namespace AgvDispatch.Application.Abstractions;

public enum TrafficSegmentReceiptState { Unavailable, Pending, Expired, PreviousSession, Invalidated, Committed }
public sealed record TrafficSegmentReceiptView(string? ReceiptId,TrafficSegmentReceiptState State,string Reason,
    DateTimeOffset? ExpiresAt,int CandidateCount)
{
    public bool ResourcesReleased=>false;
}

public sealed class TrafficSegmentCommitOptions { public bool Enabled { get; set; } }
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
