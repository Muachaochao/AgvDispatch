namespace AgvDispatch.Application.Abstractions;

public sealed record RobokitInspectionHistoryQuery(string? VehicleId = null,int MaximumCount=100);
public sealed record RobokitInspectionRecord(string Id,string VehicleId,DateTimeOffset At,string Operator,
    bool Available,bool Stable,string Reason,string? LocalMapId,string? LocalMapVersion,string? RobotMap,
    string? RobotMapMd5,int StationCount,int MissingMappingCount,string Notes);
public interface IRobokitInspectionHistory
{
    Task AppendAsync(string operatorIdentity,RobokitInspectionSnapshot snapshot,CancellationToken ct);
    Task<IReadOnlyList<RobokitInspectionRecord>> ReadAsync(RobokitInspectionHistoryQuery query,CancellationToken ct);
}
