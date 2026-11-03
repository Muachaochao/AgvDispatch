using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class DispatchOptimizationApi
{
    public static void MapDispatchOptimizationApi(this WebApplication app)
    {
        app.MapGet("/api/dispatch-pipeline", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<DispatchPipelineInspectionSnapshot>("GetDispatchPipelineInspection", new { }, ct));
        app.MapGet("/api/dispatch-optimization", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<DispatchOptimizationSnapshot>("GetDispatchOptimization", new { }, ct));
        app.MapPut("/api/dispatch-optimization/options", async (
            SaveDispatchOptimizationOptionsRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<DispatchOptimizationSnapshot>("SaveDispatchOptimizationOptions", request, ct));
    }
}
