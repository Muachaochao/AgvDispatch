namespace AgvDispatch.Application.Abstractions;

// Client supplies only persisted entry-step/resource identities, never positions or calibration.
public sealed record TrafficWaitingBayDepartureQuery(string StepId,string ResourceId);
public sealed record TrafficWaitingBaySites(string WaitingSite,string DumpingSite);
public sealed record TrafficWaitingBayLease(TrafficSegmentLease Identity,DateTimeOffset ExpiresAt);
public sealed record TrafficWaitingBayDepartureContext(TrafficSegmentReleaseContext Entry,string WaitingNodeId,
    string DumpingNodeId,string ResourceId,IReadOnlyList<TrafficWaitingBayLease> BayLeases,
    IReadOnlyList<TrafficWaitingBayLease> EntryLeases);
public sealed record TrafficWaitingBayDepartureAssessment(DateTimeOffset CheckedAt,DateTimeOffset? ValidUntil,
    string StepId,string ResourceId,string? TaskId,string? VehicleId,string? MapId,string? MapVersionId,
    bool Available,bool DepartureEvidenceVerified,string Reason,IReadOnlyList<string> LeaseIds)
{
    public bool ResourcesReleased=>false;
    public bool ReleaseAuthorized=>false;
}
public interface ITrafficWaitingBayDepartureService
{
    Task<TrafficWaitingBayDepartureAssessment> ReadAsync(TrafficWaitingBayDepartureQuery query,CancellationToken ct);
}
