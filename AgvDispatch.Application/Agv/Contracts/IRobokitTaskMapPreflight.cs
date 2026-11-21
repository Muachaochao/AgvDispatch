namespace AgvDispatch.Application.Abstractions;

public sealed record RobokitTaskRouteCheck(int Sequence,string Source,string Target,bool DirectConfirmed,string Message);
public sealed record RobokitTaskMapCheck(bool Accepted,string Message)
{public IReadOnlyList<RobokitTaskRouteCheck> RouteChecks {get;init;}=[];}
public interface IRobokitTaskMapPreflight
{
    Task<RobokitTaskMapCheck> CheckCurrentMapAsync(string vehicleId,string mapId,string versionId,CancellationToken ct) => Task.FromResult(new RobokitTaskMapCheck(false,"未配置实际出区时的当前车端地图核验。"));
    Task<RobokitTaskMapCheck> CheckRouteAsync(string vehicleId,IReadOnlyList<string> path,CancellationToken ct) => Task.FromResult(new RobokitTaskMapCheck(false,"未配置实际发送路线核验。"));
    Task<RobokitTaskMapCheck> CheckAsync(string vehicleId,string currentSite,string pickup,string waiting,string dumping,
        string mapId,string versionId,CancellationToken ct);

    async Task<RobokitTaskMapCheck> CheckWaitingSitesAsync(string vehicleId, string currentSite, string pickup,
        IReadOnlyList<string> waitingSites, string dumping, string mapId, string versionId, CancellationToken ct)
    {
        if (waitingSites.Count == 0) return new(false, "未配置倒料候车点。");
        var checks = new List<RobokitTaskRouteCheck>();
        RobokitTaskMapCheck result = new(true, "");
        foreach (var waiting in waitingSites)
        {
            result = await CheckAsync(vehicleId, currentSite, pickup, waiting, dumping, mapId, versionId, ct);
            checks.AddRange(result.RouteChecks);
            if (!result.Accepted) break;
        }
        return result with { RouteChecks = checks.ToArray() };
    }
}
