using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class RoadClosureApi
{
    public static void MapRoadClosureApi(this WebApplication app)
    {
        app.MapGet("/api/road-closures", async (DispatchPipe p, CancellationToken ct) =>
            await p.ReadAsync<RoadClosureSnapshot>("GetRoadClosures", new { }, ct));
        app.MapPost("/api/road-closures", async (RoadClosureCreateRequest q, DispatchPipe p, CancellationToken ct) =>
            await p.ReadAsync<RoadClosureResult>("CreateRoadClosure", q, ct));
        app.MapPost("/api/road-closures/release", async (RoadClosureReleaseRequest q, DispatchPipe p, CancellationToken ct) =>
            await p.ReadAsync<RoadClosureResult>("ReleaseRoadClosure", q, ct));
        app.MapPost("/api/road-closures/preview", async (RoadClosurePreviewRequest q, DispatchPipe p, CancellationToken ct) =>
            await p.ReadAsync<RoadClosurePreview>("PreviewRoadClosureRoute", q, ct));
    }
}
