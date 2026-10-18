using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class WaitingApi
{
    public static void MapWaitingApi(this WebApplication app)
    {
        app.MapPost("/api/traffic/waiting",async(TrafficWaitingQuery query,DispatchPipe pipe,CancellationToken ct)=>{
            if(query.PageNumber is <1 or >100000||query.PageSize is <1 or >50||query.MinimumPriority is <0 or >100||query.BlockingResourceId?.Length>128||!Enum.IsDefined(query.Sort))return Results.BadRequest(new{error="等待筛选/排序/分页参数无效，每页最多50条。"});
            var before=SnapshotReader.ProjectMap(await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap,new TrafficMapQuery(),ct));
            var page=await pipe.ReadAsync<TrafficWaitingPage>(TrafficControlOperations.GetTrafficWaiting,query,ct)??throw new InvalidDataException("后台未返回等待队列。");
            var after=SnapshotReader.ProjectMap(await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap,new TrafficMapQuery(),ct));
            if(before?.Id!=after?.Id||before?.Version!=after?.Version||before?.DefinitionHash!=after?.DefinitionHash)return Results.Conflict(new{error="查询期间地图发生变化，请刷新等待队列。"});
            return Results.Ok(new{Page=page,MapId=after?.Id,MapVersion=after?.Version,MapDefinitionHash=after?.DefinitionHash});
        });
    }
}
