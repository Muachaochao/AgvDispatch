using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class PlcCycleTestApi
{
    public static void MapPlcCycleTestApi(this WebApplication app)
    {
        app.MapGet("/api/fleet-test",async(DispatchPipe pipe,CancellationToken ct)=>await pipe.ReadAsync<FleetTestStatus>("GetFleetTest",new{},ct));
        app.MapPost("/api/fleet-test/start",async(FleetTestStart request,DispatchPipe pipe,CancellationToken ct)=>
        {
            if(!app.Configuration.GetValue("Web:EnableTaskSubmission",false))return Results.Json(new{error="请使用多车业务测试启动配置。"},statusCode:403);
            return Results.Ok(await pipe.ReadAsync<FleetTestStatus>("StartFleetTest",request,ct));
        });
        app.MapPost("/api/fleet-test/retire-start",async(FleetTestStartRetirement request,DispatchPipe pipe,CancellationToken ct)=>
            Results.Ok(await pipe.ReadAsync<FleetTestStartRetirementReceipt>("RetireFleetTestStart",request,ct)));
        app.MapPost("/api/fleet-test/{action}",async(string action,FleetTestControl request,DispatchPipe pipe,CancellationToken ct)=>
        {
            var method=action switch {"stop"=>"StopFleetTest","resume"=>"ResumeFleetTest","close"=>"CloseFleetTest",_=>null};
            if(method==null)return Results.NotFound();
            if(action=="resume"&&!app.Configuration.GetValue("Web:EnableTaskSubmission",false))return Results.Json(new{error="续接需要业务测试启动配置。"},statusCode:403);
            return Results.Ok(await pipe.ReadAsync<FleetTestStatus>(method,request,ct));
        });
        app.MapGet("/api/business-run", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<BusinessRunStatus>("GetBusinessRun", new { }, ct));
        app.MapPost("/api/business-run/start", async (BusinessRunStart request, DispatchPipe pipe, CancellationToken ct) =>
        {
            if (!app.Configuration.GetValue("Web:EnableTaskSubmission", false))
                return Results.Json(new { error = "Web任务提交未启用。" }, statusCode: 403);
            return Results.Ok(await pipe.ReadAsync<BusinessRunStatus>("StartBusinessRun", request, ct));
        });
        app.MapPost("/api/business-run/stop", async (BusinessRunStop request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<BusinessRunStatus>("StopBusinessRun", request, ct));
        app.MapPost("/api/business-run/resume", async (BusinessRunResume request, DispatchPipe pipe, CancellationToken ct) =>
        {
            if (!app.Configuration.GetValue("Web:EnableTaskSubmission",false))
                return Results.Json(new { error="续接业务运行需要显式启用业务执行模式。" },statusCode:403);
            return Results.Ok(await pipe.ReadAsync<BusinessRunStatus>("ResumeBusinessRun",request,ct));
        });
        app.MapPost("/api/business-run/close-reviewed", async (BusinessRunStop request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<BusinessRunStatus>("CloseReviewedBusinessRun", request, ct));
        app.MapGet("/api/plc-cycle-test", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<PlcCycleTestStatus>("GetPlcCycleTest", new { }, ct));
        app.MapPost("/api/plc-cycle-test/arm", async (PlcCycleTestArmRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<PlcCycleTestStatus>("ArmPlcCycleTest", request, ct));
        app.MapPost("/api/plc-cycle-test/stop", async (PlcCycleTestStopRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<PlcCycleTestStatus>("StopPlcCycleTest", request, ct));
        app.MapPost("/api/plc-cycle-test/reset", async (PlcCycleTestResetRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<PlcCycleTestStatus>("ResetPlcCycleTest", request, ct));
    }
}
