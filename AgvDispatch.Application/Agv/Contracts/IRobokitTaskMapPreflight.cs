namespace AgvDispatch.Application.Abstractions;

public sealed record RobokitTaskRouteCheck(int Sequence,string Source,string Target,bool DirectConfirmed,string Message);
public sealed record RobokitTaskMapCheck(bool Accepted,string Message)
{public IReadOnlyList<RobokitTaskRouteCheck> RouteChecks {get;init;}=[];}
public interface IRobokitTaskMapPreflight
{
    Task<RobokitTaskMapCheck> CheckRouteAsync(string vehicleId,IReadOnlyList<string> path,CancellationToken ct) => Task.FromResult(new RobokitTaskMapCheck(false,"未配置实际发送路线核验。"));
    Task<RobokitTaskMapCheck> CheckAsync(string vehicleId,string currentSite,string pickup,string waiting,string dumping,
        string mapId,string versionId,CancellationToken ct);
}
