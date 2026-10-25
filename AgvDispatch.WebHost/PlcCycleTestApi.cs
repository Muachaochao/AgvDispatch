using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class PlcCycleTestApi
{
    public static void MapPlcCycleTestApi(this WebApplication app)
    {
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
