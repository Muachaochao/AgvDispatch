using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class WaitingApi
{
    public sealed record BlockingRequest(string TaskId,string StepId,string? ExpectedMapId,string? ExpectedMapVersion,string? ExpectedMapDefinitionHash);
    public sealed record DepartureRequest(string StepId,string ResourceId,string? ExpectedMapId,string? ExpectedMapVersion,string? ExpectedMapDefinitionHash);
    public static void MapWaitingApi(this WebApplication app)
    {
        app.MapPost("/api/traffic/waiting-bay-departure",async(DepartureRequest query,DispatchPipe pipe,CancellationToken ct)=>{
            if(string.IsNullOrWhiteSpace(query.StepId)||query.StepId.Length>64||string.IsNullOrWhiteSpace(query.ResourceId)||query.ResourceId.Length>128)
                return Results.BadRequest(new{error="原入站步骤或候车资源编号无效。"});
            var before=SnapshotReader.ProjectMap(await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap,new TrafficMapQuery(),ct));
            if(before is null||string.IsNullOrWhiteSpace(query.ExpectedMapDefinitionHash)||before.Id!=query.ExpectedMapId||before.Version!=query.ExpectedMapVersion||before.DefinitionHash!=query.ExpectedMapDefinitionHash)
                return Results.Conflict(new{error="地图上下文已变化，请重新打开任务。"});
            var result=await pipe.ReadAsync<TrafficWaitingBayDepartureAssessment>(TrafficControlOperations.GetWaitingBayDeparture,
                new TrafficWaitingBayDepartureQuery(query.StepId,query.ResourceId),ct)??throw new InvalidDataException("后台未返回候车位离位核验。");
            var after=SnapshotReader.ProjectMap(await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap,new TrafficMapQuery(),ct));
            if(after is null||before.Id!=after.Id||before.Version!=after.Version||before.DefinitionHash!=after.DefinitionHash||
                result.MapId is {} id&&id!=before.Id||result.MapVersionId is {} version&&version!=before.Version)
                return Results.Conflict(new{error="核验期间地图变化，请重新检查。"});
            return Results.Ok(new{Assessment=result,MapId=after.Id,MapVersion=after.Version,MapDefinitionHash=after.DefinitionHash});
        });
        app.MapPost("/api/traffic/blocking",async(BlockingRequest query,DispatchPipe pipe,CancellationToken ct)=>{
            if(string.IsNullOrWhiteSpace(query.TaskId)||query.TaskId.Length>64||string.IsNullOrWhiteSpace(query.StepId)||query.StepId.Length>64)return Results.BadRequest(new{error="原任务/步骤编号无效。"});
            var before=SnapshotReader.ProjectMap(await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap,new TrafficMapQuery(),ct));
            if(before?.Id!=query.ExpectedMapId||before?.Version!=query.ExpectedMapVersion||before?.DefinitionHash!=query.ExpectedMapDefinitionHash)return Results.Conflict(new{error="地图上下文已变化，请从新等待记录重新打开。"});
            var detail=await pipe.ReadAsync<TrafficBlockingDetails>(TrafficControlOperations.GetTrafficBlocking,new TrafficBlockingQuery(query.TaskId,query.StepId),ct)??throw new InvalidDataException("后台未提供阻塞链详情。");
            var after=SnapshotReader.ProjectMap(await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap,new TrafficMapQuery(),ct));
            if(before?.Id!=after?.Id||before?.Version!=after?.Version||before?.DefinitionHash!=after?.DefinitionHash)return Results.Conflict(new{error="查询期间地图变化，请刷新等待记录。"});
            return Results.Ok(new{Detail=detail,MapId=after?.Id,MapVersion=after?.Version,MapDefinitionHash=after?.DefinitionHash});
        });
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
