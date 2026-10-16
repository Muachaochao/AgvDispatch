using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Application.Agv;

public sealed record AgvTrackReplayValidation(bool Available,string Reason,IReadOnlyList<AgvTrackPointSnapshot> Points);
public static class AgvTrackReplayValidator
{
    public static AgvTrackReplayValidation Validate(IReadOnlyList<AgvTrackPointSnapshot> points,AgvTrackQueryRequest query)
    {
        AgvTrackReplayValidation Refuse(string reason)=>new(false,reason,[]);
        if(points is null||query is null||string.IsNullOrWhiteSpace(query.MapVersionId)||string.IsNullOrWhiteSpace(query.MapId)||string.IsNullOrWhiteSpace(query.VehicleId)||query.To<=query.From||query.To-query.From>TimeSpan.FromDays(1))
            return Refuse("回放须指定车辆、地图版本及24小时内的有效范围。");
        if(points.Count>Math.Clamp(query.MaximumCount,2,10000))return Refuse("回放返回点数超出请求预算。");
        if(points.Any(p=>p is null||p.VehicleId!=query.VehicleId||p.MapId!=query.MapId||p.MapVersionId!=query.MapVersionId||p.SampledAt<query.From||p.SampledAt>query.To))
            return Refuse("轨迹包含其他车辆、地图版本或范围外记录，拒绝混合回放。");
        if(points.Any(p=>p.Id<=0||!double.IsFinite(p.X)||!double.IsFinite(p.Y)||Math.Abs(p.X)>1e6||Math.Abs(p.Y)>1e6||
            p.Angle is {} angle&&!double.IsFinite(angle)||p.PositionState is not (AgvMapPositionState.Normal or AgvMapPositionState.OffRoute)))
            return Refuse("轨迹存在无效坐标或无效定位状态，不能拼接为有效历史路线。");
        if(points.Select(p=>p.Id).Distinct().Count()!=points.Count)return Refuse("轨迹点编号重复。");
        return new(true,"仅回放指定版本的历史位置，不代表实时定位或通行许可。",points.OrderBy(p=>p.SampledAt).ThenBy(p=>p.Id).ToArray());
    }
    public static bool CanConnect(AgvTrackPointSnapshot a,AgvTrackPointSnapshot b)=>a.VehicleId==b.VehicleId&&a.MapId==b.MapId&&a.MapVersionId==b.MapVersionId&&
        b.SampledAt>=a.SampledAt&&b.SampledAt-a.SampledAt<=TimeSpan.FromSeconds(30);
}
