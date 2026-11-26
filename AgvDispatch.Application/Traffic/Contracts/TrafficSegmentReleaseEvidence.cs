using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Application.Abstractions;

// Application contracts, not claims about fields provided by any Robokit firmware.
public sealed record TrafficProgressFrame(string TaskId,string StepId,string VehicleId,string VendorTaskId,
    string CommandId,string RouteRevision,string MapId,string MapVersionId,string CoordinateFrameId,
    string ControllerEpoch,long Sequence,int LastFullyTraversedEdgeIndex,double X,double Y,
    double LocalizationConfidence,double PositionUncertaintyMeters,SignalQuality Quality,bool Online,
    bool Coherent,DateTimeOffset ObservedAt);
public sealed record TrafficProgressPair(TrafficProgressFrame First,TrafficProgressFrame Second);
public interface IAuthoritativeTrafficProgressReader
{
    Task<TrafficProgressPair?> ReadAsync(string vehicleId,CancellationToken ct);

    Task<TrafficProgressPair?> ReadAfterAsync(string vehicleId, TrafficProgressFrame anchor, CancellationToken ct) =>
        ReadAsync(vehicleId, ct);
}
public sealed record TrafficBodyEnvelope(string ProfileId,string VehicleId,string CoordinateFrameId,
    double RadiusFromReportedReferenceMeters,bool CalibrationVerified,bool IncludesMaximumPermittedLoad,
    double? LengthMeters=null,double? WidthMeters=null);
public sealed record TrafficSegmentLease(string LeaseId,string ResourceId,string TaskId,string StepId,
    string VehicleId,string MapId,string MapVersionId);
public sealed record TrafficSegmentReleaseContext(string TaskId,string StepId,string VehicleId,string VendorTaskId,
    string CommandId,string RouteRevision,string MapId,string MapVersionId,string CoordinateFrameId,
    DateTimeOffset SendIntentAt,bool TaskAndStepExecuting,bool RecoveryAndMapGateOpen,bool EvidenceSourceRegistered,
    MapDefinition Map,IReadOnlyList<string> RouteNodeIds,IReadOnlyList<string> RouteEdgeIds,
    IReadOnlyList<TrafficSegmentLease> Leases,TrafficBodyEnvelope? Body);
public sealed class TrafficSegmentReleasePolicy
{
    public bool Enabled { get; set; }
    public double MinimumObservationIntervalSeconds { get; set; } = 0.2;
    public double MaximumAgeSeconds { get; set; } = 5;
    public double MinimumConfidence { get; set; } = 0.9;
    public double MaximumUncertaintyMeters { get; set; } = 0.25;
    public double MaximumRouteDeviationMeters { get; set; } = 0.5;
    public double SafetyMarginMeters { get; set; } = 0.15;
}
public sealed record TrafficSegmentReleaseCandidate(string LeaseId,string ResourceId,bool EvidenceVerified,string Reason);
public sealed record TrafficSegmentProgressSummary(int FirstLastFullyTraversedEdgeIndex,
    int SecondLastFullyTraversedEdgeIndex,double FirstX,double FirstY,double SecondX,double SecondY,
    double FirstPositionUncertaintyMeters,double SecondPositionUncertaintyMeters,string BodyProfileId,
    double BodyRadiusMeters,DateTimeOffset FirstObservedAt,DateTimeOffset SecondObservedAt);
public sealed record TrafficSegmentReleaseAssessment(bool Available,string Reason,IReadOnlyList<TrafficSegmentReleaseCandidate> Candidates)
{
    public bool ResourcesReleased => false;
    public TrafficSegmentProgressSummary? Progress { get; init; }
}
