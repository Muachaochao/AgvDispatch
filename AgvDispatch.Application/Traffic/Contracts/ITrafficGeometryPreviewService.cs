namespace AgvDispatch.Application.Abstractions;

// Client supplies only original database step identities, never geometry or calibration.
public sealed record TrafficGeometryPreviewQuery(string FirstStepId,string SecondStepId);
public sealed record TrafficGeometryRouteSummary(string TaskId,string StepId,string VehicleId,string CommandId,string RouteRevision,int EdgeCount);
public sealed record TrafficGeometryPreviewSnapshot(DateTimeOffset CheckedAt,DateTimeOffset? ValidUntil,
    string? MapId,string? MapVersionId,TrafficGeometryRouteSummary? First,TrafficGeometryRouteSummary? Second,string Reason,TrafficGeometryPreview? Assessment)
{
    public bool Available=>Assessment?.Available==true;
    public bool ResourcesLocked=>false;
    public bool ResourcesReleased=>false;
}
public interface ITrafficGeometryPreviewService
{
    Task<TrafficGeometryPreviewSnapshot> ReadAsync(TrafficGeometryPreviewQuery query,CancellationToken ct);
}
// Backend-only certified source. No production registration until actual calibration is confirmed.
public sealed record TrafficGeometryCertifiedProfile(TrafficBodyEnvelope Body,double PositionUncertaintyMeters,string Revision,DateTimeOffset VerifiedUntil);
public interface ITrafficGeometryProfileProvider
{
    Task<TrafficGeometryCertifiedProfile?> ReadAsync(string vehicleId,CancellationToken ct);
}
