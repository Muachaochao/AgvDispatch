using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class DiagnosticsApi
{
    public sealed record WebCancelRequest(string RequestId,string TaskId,AgvDispatch.Domain.Tasks.DispatchTaskStage? ExpectedStage,string Reason);
    public static void MapDiagnosticsApi(this WebApplication app,bool recoveryEnabled,bool taskOperationsEnabled=false)
    {
        app.MapPost("/api/tasks/{id}/cancel",async(string id,WebCancelRequest request,DispatchPipe pipe,CancellationToken ct)=>
        {
            if(!taskOperationsEnabled)return Results.Json(new{error="Web任务取消未启用；取消能力与新建任务权限分别配置。"},statusCode:403);
            if(request.ExpectedStage is null)return Results.BadRequest(new{error="必须提供原观察expectedStage；不接受缺省状态。"});
            if(request.TaskId!=id)return Results.BadRequest(new{error="任务身份不一致。"});
            return Results.Ok(await pipe.ReadAsync<TaskCancelResult>("CancelDispatchTask",new TaskCancelRequest(request.RequestId,request.TaskId,request.ExpectedStage.Value,request.Reason),ct));
        });
        app.MapPost("/api/commissioning",async(CommissioningQuery query,DispatchPipe pipe,CancellationToken ct)=>
        {
            if(query.StationId==null||query.VehicleId==null||query.StationId.Length>32||query.VehicleId.Length>32)return Results.BadRequest(new{error="工位或车辆编号无效。"});
            return Results.Ok(await pipe.ReadAsync<CommissioningReport>("GetCommissioningReport",query,ct));
        });
        app.MapPost("/api/tasks/{id}/runtime-events",async(string id,TaskRuntimeQuery query,DispatchPipe pipe,CancellationToken ct)=>
        {
            TaskRuntimeQueryValidation.Validate(query);
            if(query.TaskId!=id)return Results.BadRequest(new{error="任务身份不一致。"});
            return Results.Ok(await pipe.ReadAsync<TaskRuntimePage>("GetTaskRuntimeEvents",query,ct));
        });
        app.MapGet("/api/tasks/{id}/diagnostics/export",async(string id,HttpContext ctx,DispatchPipe pipe,CancellationToken ct)=>
        {
            var archive=await pipe.ReadAsync<TaskDiagnosticArchive>("ExportTaskDiagnostics",new TaskDetailRequest(id,false),ct)
                ??throw new InvalidDataException("后台未返回诊断包。");
            var bytes=Convert.FromBase64String(archive.Base64);
            if(bytes.Length>6*1024*1024||!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),archive.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("诊断包校验失败。");
            ctx.Response.Headers["X-Content-SHA256"]=archive.Sha256;
            return Results.File(bytes,"application/zip","agv-diagnostics.zip");
        });
        app.MapGet("/api/recovery",async(DispatchPipe pipe,CancellationToken ct)=>
        {
            var status=await pipe.ReadAsync<RecoveryStatus>("GetExecutionRecovery",new{},ct)??throw new InvalidDataException("后台未返回恢复状态。");
            // Raw recovery Evidence contains device identity baselines; expose review summaries only.
            return new {status.SessionId,status.Released,status.Message,recoveryEnabled,
                Items=status.Items.Select(i=>new{i.TicketId,i.TaskId,i.StepId,i.StepText,i.State,i.Reason,i.Verified,i.Revision,i.VehicleId,i.VehicleSummary,i.PlcSummary,i.ResourceSummary,i.IdentitySummary,i.IdentityState,i.IdentityBlocksRecovery,i.LoadSummary}),
                Audits=status.Audits.Take(50).Select(a=>new{a.Id,a.TaskId,a.StepId,a.Operator,a.DecisionText,a.Reason,a.OccurredAt})};
        });
        app.MapPost("/api/recovery/begin",async(TaskRecoveryReviewRequest request,DispatchPipe pipe,CancellationToken ct)=>
            recoveryEnabled?Results.Ok(await pipe.ReadAsync<RecoveryResult>("BeginTaskRecoveryReview",request,ct)):Disabled());
        app.MapPost("/api/recovery/decisions",async(RecoveryDecisionRequest request,DispatchPipe pipe,CancellationToken ct)=>
            recoveryEnabled?Results.Ok(await pipe.ReadAsync<RecoveryResult>("DecideExecutionRecovery",request,ct)):Disabled());
        app.MapPost("/api/recovery/release",async(RecoveryReleaseRequest request,DispatchPipe pipe,CancellationToken ct)=>
            recoveryEnabled?Results.Ok(await pipe.ReadAsync<RecoveryResult>("ReleaseExecutionRecovery",request,ct)):Disabled());
    }
    private static IResult Disabled()=>Results.Json(new{error="Web恢复写操作未启用；可只读核对。启用后仍由后台验证权限、批次、版本和现场条件。"},statusCode:403);
}
