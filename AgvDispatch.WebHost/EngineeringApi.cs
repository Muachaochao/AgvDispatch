using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class EngineeringApi
{
    public sealed record BindingRequest(string ExpectedRevision,AgvMapBindingSetting[] Bindings);
    public static void MapEngineeringApi(this WebApplication app)
    {
        app.MapGet("/api/communication", async (DispatchPipe p, CancellationToken ct) =>
            await p.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct));
        app.MapPost("/api/communication", async (SaveCommunicationSettingsRequest q, DispatchPipe p, CancellationToken ct) =>
            await p.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.SaveCommunicationSettings, q, ct));
        app.MapGet("/api/task-parameters",async(DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<TaskParameterSnapshot>(TrafficControlOperations.GetTaskParameters,new{},ct));
        app.MapPost("/api/task-parameters/validate",async(TaskParameterRequest q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<TaskParameterResult>(TrafficControlOperations.ValidateTaskParameters,q,ct));
        app.MapPost("/api/task-parameters/save",async(TaskParameterRequest q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<TaskParameterResult>(TrafficControlOperations.SaveTaskParameters,q,ct));
        app.MapGet("/api/map-bindings",async(DispatchPipe p,CancellationToken ct)=>{
            var s=await p.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings,new{},ct)??throw new InvalidDataException("后台没有配置");
            return new{s.Revision,Bindings=s.Saved.MapBindings??[],s.CanManage,s.RestartRequired};
        });
        app.MapPost("/api/map-bindings",async(BindingRequest q,DispatchPipe p,CancellationToken ct)=>{
            if(q.Bindings is null||q.Bindings.Length>100)return Results.BadRequest(new{error="绑定最多100条"});
            var s=await p.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings,new{},ct)??throw new InvalidDataException("后台没有配置");
            if(s.Revision!=q.ExpectedRevision)return Results.Conflict(new{error="配置已变化，请重新读取，不覆盖其他操作"});
            s.Saved.MapBindings=q.Bindings.ToList();
            var saved=await p.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.SaveCommunicationSettings,new SaveCommunicationSettingsRequest(q.ExpectedRevision,s.Saved),ct);
            return Results.Ok(new{saved!.Revision,Bindings=saved.Saved.MapBindings??[],saved.CanManage,saved.RestartRequired});
        });
        app.MapPost("/api/map-bindings/inspect",async(RobokitInspectionQuery q,DispatchPipe p,CancellationToken ct)=>await p.ReadAsync<RobokitInspectionSnapshot>(TrafficControlOperations.GetRobokitInspection,q,ct));
    }
}
