using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class EditorApi
{
    public static void MapEditorApi(this WebApplication app)
    {
        app.MapGet("/api/maps/{id}/workspace", async (string id, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace, new { MapId = id }, ct));
        app.MapPost("/api/maps/create", async (CreateMapRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.CreateMap, request, ct));
        app.MapPost("/api/maps/draft", async (SaveMapDraftRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.SaveMapDraft, request, ct));
        app.MapPost("/api/maps/publish", async (PublishMapRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.PublishMap, request, ct));
        app.MapGet("/api/maps/{id}/versions", async (string id, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapVersionHistoryItem[]>(TrafficControlOperations.GetMapVersionHistory, new { MapId = id }, ct));
        // User selects a local file. No client-supplied filesystem path is accepted.
        app.MapPost("/api/maps/import", async (HttpRequest request, DispatchPipe pipe, CancellationToken ct) =>
        {
            var format = request.Query["format"].ToString();
            if (format is not ("smap" or "json")) return Results.BadRequest(new { error = "仅支持SMAP或规范化JSON。" });
            using var bytes = new MemoryStream();
            await request.Body.CopyToAsync(bytes, ct);
            if (bytes.Length is < 2 or > 8 * 1024 * 1024) return Results.BadRequest(new { error = "地图文件应为2字节至8MiB。" });
            var directory = Path.Combine(Path.GetTempPath(), "agv-web-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "map." + format);
            try
            {
                await File.WriteAllBytesAsync(path, bytes.ToArray(), ct);
                var result = await pipe.ReadAsync<MapImportPreviewResult>(TrafficControlOperations.PreviewMapImport,
                    new PreviewMapImportRequest(path, format == "smap" ? MapImportFormat.RobokitSmap : MapImportFormat.NormalizedJson), ct);
                // The temporary upload is not a lasting map source or background.
                return Results.Ok(result is null ? null : result with { Definition = result.Definition is null ? null : result.Definition with { Source = "" } });
            }
            finally { File.Delete(path); Directory.Delete(directory); }
        });
        app.MapGet("/api/vehicles/configuration", async (DispatchPipe pipe, CancellationToken ct) =>
        {
            var settings = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct);
            return settings is null ? null : new { settings.Revision, Vehicles = settings.Saved.Agvs, settings.RestartRequired, settings.CanManage };
        });
        app.MapPost("/api/vehicles/configuration", async (VehicleConfigurationRequest request, DispatchPipe pipe, CancellationToken ct) =>
        {
            if (request.Vehicles is null || request.Vehicles.Length > 100) return Results.BadRequest(new { error = "车辆配置超出100辆限制。" });
            var settings = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct)
                ?? throw new InvalidDataException("没有通信配置。");
            if (settings.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "通信配置已经变化，请重新读取。" });
            // Preserve PLC, map bindings, global communication switches and protocol settings.
            settings.Saved.Agvs = request.Vehicles.ToList();
            var saved = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.SaveCommunicationSettings,
                new SaveCommunicationSettingsRequest(request.ExpectedRevision, settings.Saved), ct);
            return Results.Ok(saved is null ? null : new { saved.Revision, Vehicles = saved.Saved.Agvs, saved.RestartRequired, saved.CanManage });
        });

        app.MapPost("/api/maps/activation-review", async (MapActivationReviewQuery query, DispatchPipe pipe, CancellationToken ct) =>
        {
            if(new[]{query.ReleaseId,query.ExpectedMapId,query.ExpectedVersionId}.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>64))return Results.BadRequest(new{error="原切换/目标地图/版本编号无效。"});
            return Results.Ok(await pipe.ReadAsync<MapActivationReview>(TrafficControlOperations.ReviewMapActivation, query, ct));
        });
        app.MapPost("/api/maps/activate", async (ActivateMapRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.ActivateMap, request, ct));
        app.MapGet("/api/maps/switch", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapSwitchStatus>(TrafficControlOperations.GetMapSwitch, new { }, ct));
        app.MapPost("/api/maps/switch/begin", async (BeginMapSwitchRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapSwitchResult>(TrafficControlOperations.BeginMapSwitch, request, ct));
        app.MapPost("/api/maps/switch/drain", async (FinishMapSwitchRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapSwitchResult>(TrafficControlOperations.ContinueMapDrain, request, ct));
        app.MapPost("/api/maps/switch/resume", async (FinishMapSwitchRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapSwitchResult>(TrafficControlOperations.ResumeMapSwitch, request, ct));
        app.MapPost("/api/maps/switch/cancel", async (FinishMapSwitchRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapSwitchResult>(TrafficControlOperations.CancelMapSwitch, request, ct));
    }
    public sealed record VehicleConfigurationRequest(string ExpectedRevision, AgvConnectionSetting[] Vehicles);
}
