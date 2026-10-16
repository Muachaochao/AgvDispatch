namespace AgvDispatch.Application.Abstractions;

public sealed record RobokitInspectionQuery(string VehicleId, string? SourceNodeId = null, string? TargetNodeId = null);
public sealed record RobokitStation(string Id, string? Type, double? X, double? Y, double? Angle, string? Description);
public sealed record RobokitNodeMapping(string NodeId, string? SiteId, bool Found, string Reason);
public sealed record RobokitInspectionSnapshot(
    string VehicleId, DateTimeOffset CheckedAt, bool Available, bool Stable,
    string Reason, string? RobokitVersion, string? ProtocolVersion,
    string? RobotMap, string? RobotMapMd5, int? LoadMapStatus,
    string? LocalMapId, string? LocalMapVersion, bool? ControlLocked, string? ControlOwner,
    IReadOnlyList<RobokitStation> Stations, IReadOnlyList<RobokitNodeMapping> Mappings,
    IReadOnlyList<string> Route, IReadOnlyList<string> Issues);
public interface IRobokitInspectionService
{
    Task<RobokitInspectionSnapshot> ReadAsync(RobokitInspectionQuery query, CancellationToken ct);
}
