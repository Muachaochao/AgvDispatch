using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficGeometryRoute(string TaskId,string StepId,string VehicleId,string MapId,
    string MapVersionId,string CoordinateFrameId,IReadOnlyList<string> NodeIds,IReadOnlyList<string> EdgeIds,
    TrafficBodyEnvelope? Body,double PositionUncertaintyMeters);
public sealed class TrafficGeometryPreviewPolicy
{
    public bool Enabled{get;set;}
    public double SafetyMarginMeters{get;set;}=.15;
    public int MaximumEdgesPerRoute{get;set;}=2000;
    public int MaximumComparisons{get;set;}=100000;
    public int MaximumReportedConflicts{get;set;}=200;
    public bool UseSpatialIndex{get;set;}=true;
    public int MaximumIndexVisits{get;set;}=1000000;
}
public enum TrafficGeometryConflictKind{LineEnvelopeOverlap,ConservativeCurveOverlap}
public sealed record TrafficSweptArea(string EdgeId,MapEdgeGeometryKind GeometryKind,MapGeometryBounds Bounds,double EnvelopeRadius);
public sealed record TrafficGeometryConflict(string FirstEdgeId,string SecondEdgeId,TrafficGeometryConflictKind Kind,string Reason);
public sealed record TrafficGeometryPreview(bool Available,bool Complete,string Reason,int Comparisons,
    IReadOnlyList<TrafficSweptArea> FirstAreas,IReadOnlyList<TrafficSweptArea> SecondAreas,IReadOnlyList<TrafficGeometryConflict> Conflicts)
{
    public int IndexVisits{get;init;}
    public bool CanConcludeSeparated=>Available&&Complete&&Conflicts.Count==0;
    public bool ResourcesLocked=>false;
    public bool ResourcesReleased=>false;
}
