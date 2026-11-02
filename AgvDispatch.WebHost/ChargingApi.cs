using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;

public static class ChargingApi
{
    public static void MapChargingApi(this WebApplication app)
    {
        app.MapGet("/api/charging", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<ChargingStatus>("GetCharging", new {}, ct));
        app.MapPost("/api/charging/request", async (ChargingRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<ChargingResult>("RequestCharging", request, ct));
        app.MapPost("/api/charging/stop", async (ChargingStopRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<ChargingResult>("StopCharging", request, ct));
        app.MapPost("/api/charging/resolve", async (ChargingResolveRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<ChargingResult>("ResolveCharging", request, ct));
    }
}
