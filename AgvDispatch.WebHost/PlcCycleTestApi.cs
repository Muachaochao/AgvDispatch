using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class PlcCycleTestApi
{
    public static void MapPlcCycleTestApi(this WebApplication app)
    {
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
