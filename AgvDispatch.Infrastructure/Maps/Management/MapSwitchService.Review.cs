using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed partial class MapSwitchService
{
    public async Task<MapActivationReview> ReviewActivationAsync(TrafficUserContext user,MapActivationReviewQuery query,CancellationToken ct)
    {
        Demand(user,TrafficPermission.View);
        foreach(var id in new[]{query.ReleaseId,query.ExpectedMapId,query.ExpectedVersionId})
            if(string.IsNullOrWhiteSpace(id)||id.Length>64)throw new ArgumentException("原切换/目标地图/版本编号为空或超过64字。");
        using var control=await gate.EnterControlAsync(ct);
        MapActivationReview? result=null;
        var transaction=await database.Ado.UseTranAsync(async()=>{
            var row=await database.Queryable<MapSwitchEntity>().Where(r=>r.ReleaseId==query.ReleaseId).FirstAsync(ct);
            var pointers=await database.Queryable<MapProjectEntity>().Where(p=>p.IsActive).Take(2).ToListAsync(ct);
            var active=pointers.Count==1?pointers[0]:null;
            var version=active?.ActiveVersionId is not null?await database.Queryable<MapVersionEntity>().Where(v=>v.MapId==active.MapId&&v.VersionId==active.ActiveVersionId).FirstAsync(ct):null;
            var running=runtime.Current;var dbHash=DefinitionHash(version?.DefinitionJson);var runtimeHash=running is null?null:DefinitionHash(JsonSerializer.Serialize(running.Definition,JsonOptions));
            var empty=pointers.Count==0&&running is null;
            var consistent=empty||(pointers.Count==1&&active?.MapId==running?.MapId&&active?.ActiveVersionId==running?.VersionId&&dbHash is not null&&dbHash==runtimeHash);
            var target=consistent&&active?.MapId==query.ExpectedMapId&&active?.ActiveVersionId==query.ExpectedVersionId;
            var previous=consistent&&active?.MapId==row?.PreviousMapId&&active?.ActiveVersionId==row?.PreviousVersionId;
            var outcome=row is null?"Unknown":row.MapId!=query.ExpectedMapId||row.VersionId!=query.ExpectedVersionId?"IdentityMismatch":
                row.Phase is 90 or 100?"ClosedHistory":!consistent?"Inconsistent":row.Phase==20&&target?"CommittedAwaitingReview":row.Phase==10&&previous?"OriginalMapObserved":"Inconsistent";
            var message=outcome switch{
                "CommittedAwaitingReview"=>"已观察到原切换提交与目标地图一致，仍需原安全流程核对；不重发激活或自动放行。",
                "OriginalMapObserved"=>"仅观察到原图和收尾阶段，不能证明原请求从未到达或稍后不会到达；禁止自动重试。",
                "ClosedHistory"=>"原切换已有关闭历史；这不是当前允许派发或再次激活的依据。",
                "IdentityMismatch"=>"原切换编号与预期目标不一致，不能借其他切换认定结果。",
                "Inconsistent"=>"持久化阶段、活动指针或运行图不一致，保留冻结并核对原记录。",
                _=>"未找到原切换，不等于未执行；请确认原数据库/后台身份，不自动重试。"};
            result=new(DateTimeOffset.UtcNow,query.ReleaseId,query.ExpectedMapId,query.ExpectedVersionId,outcome,message,row is null?null:Snapshot(row),active?.MapId,active?.ActiveVersionId,dbHash,running?.MapId,running?.VersionId,runtimeHash,gate.Mode,gate.CanStartNewWork,consistent,
                ["按原切换编号查询持久化记录，不受最近30条历史列表限制。","数据库指针/阶段在同一只读事务内读取，运行图由原控制门保护。","SHA256只核对本地地图内容，不是厂家MD5或坐标对齐证明。","本接口不激活、取消、恢复派发、释放占用或写设备；门状态不是现场放行许可。"]);
        });
        if(!transaction.IsSuccess)throw new InvalidOperationException("地图激活结果只读复核失败。",transaction.ErrorException);
        return result!;
    }
    private static string? DefinitionHash(string? json)
    {
        if(json is null)return null;
        try{var definition=JsonSerializer.Deserialize<MapDefinition>(json,JsonOptions);return definition is null?null:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition,JsonOptions))));}
        catch(JsonException){return null;}
    }
}
