namespace AgvDispatch.Domain.Maps;

// Imported evidence only. No member is a vehicle command or effective controller setting.
public sealed record RobokitMapSource(string Sha256,string PlaneSha256,string MapName,string FormatVersion,
    int CurveCount,int ConvertedCurveCount);
public sealed record RobokitPathEvidence(int SchemaVersion,string SourceSha256,string PlaneSha256,int CurveIndex,
    string InstanceName,string ClassName,string FromSite,string ToSite,bool PropertyListPresent,string? RawPropertiesJson,
    string ImportedEdgeId,string ImportedFrom,string ImportedTo,double FromX,double FromY,double ToX,double ToY,
    MapEdgeGeometryKind GeometryKind,IReadOnlyList<MapPoint2D>? ControlPoints);
