using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class EditorApi
{
    public static void MapEditorApi(this WebApplication app)
    {
        app.MapPost("/api/maps/prepare-runtime", async (PrepareRuntimeMapRequest request, VendorMapSourceArchive archive, DispatchPipe pipe, CancellationToken ct) =>
        {
            if (!request.RobotCoordinatesConfirmed || request.Roles is null || request.Roles.Count is < 1 or > 5000)
                return Results.BadRequest(new { error = "请逐站确认用途，并确认使用车端原始坐标。" });
            var bytes = await archive.ReadAsync(request.SourceSha256, ct);
            if (bytes is null)
            {
                // Downloads from earlier builds were kept here before the shared source archive existed.
                var previous = Path.Combine(app.Environment.ContentRootPath, "data", "robot-map-downloads", request.SourceSha256 + ".smap");
                if (File.Exists(previous) && new FileInfo(previous).Length <= 8 * 1024 * 1024)
                {
                    bytes = await File.ReadAllBytesAsync(previous, ct);
                    if (await archive.SaveAsync(bytes, ct) != request.SourceSha256)
                        return Results.BadRequest(new { error = "原下载文件与地图摘要不一致，请重新拉取。" });
                }
            }
            if (bytes is null) return Results.NotFound(new { error = "原始地图未留存，请重新拉取机器人地图或导入原SMAP。" });
            var directory = Path.Combine(Path.GetTempPath(), "agv-runtime-map", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "source.smap");
            try
            {
                await File.WriteAllBytesAsync(path, bytes, ct);
                var result = await pipe.ReadAsync<MapImportPreviewResult>(TrafficControlOperations.PreviewMapImport,
                    new PreviewMapImportRequest(path, MapImportFormat.RobokitSmap) { RuntimeRoles = request.Roles,
                        ExpectedSourceSha256 = request.SourceSha256, RobotCoordinatesConfirmed = true }, ct);
                return Results.Ok(result is null ? null : result with { Definition = result.Definition is null ? null : result.Definition with { Source = "" } });
            }
            finally { File.Delete(path); Directory.Delete(directory); }
        });
        app.MapGet("/api/maps/{id}/workspace", async (string id, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace, new { MapId = id }, ct));
        app.MapPost("/api/maps/archive",async(ArchiveMapRequest request,DispatchPipe pipe,CancellationToken ct)=>
            await pipe.ReadAsync<MapOperationResult>("ArchiveMapProject",request,ct));
        app.MapPost("/api/maps/create", async (CreateMapRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.CreateMap, request, ct));
        app.MapPost("/api/maps/draft", async (SaveMapDraftRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.SaveMapDraft, request, ct));
        app.MapPost("/api/maps/publish", async (PublishMapRequest request, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.PublishMap, request, ct));
        app.MapGet("/api/maps/{id}/versions", async (string id, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapVersionHistoryItem[]>(TrafficControlOperations.GetMapVersionHistory, new { MapId = id }, ct));
        app.MapGet("/api/maps/{id}/versions/{versionId}", async (string id, string versionId, DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<MapVersionSnapshot>(TrafficControlOperations.GetMapVersion, new { MapId = id, VersionId = versionId }, ct));
        // User selects a local file. No client-supplied filesystem path is accepted.
        app.MapPost("/api/maps/import", async (HttpRequest request, DispatchPipe pipe,VendorMapSourceArchive archive, CancellationToken ct) =>
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
                if(format=="smap"&&result?.Accepted==true&&result.Definition is not null)
                {
                    var sha=await archive.SaveAsync(bytes.ToArray(),ct);
                    if(result.Definition.RobokitSource?.Sha256!=sha)throw new InvalidDataException("导入解析与原图留存摘要不一致，请更新匹配后台。");
                }
                // Do not expose the temporary upload or archive filesystem path to browsers.
                return Results.Ok(result is null ? null : result with { Definition = result.Definition is null ? null : result.Definition with { Source = "" } });
            }
            finally { File.Delete(path); Directory.Delete(directory); }
        });
        app.MapGet("/api/maps/vendor-source/{sha}",async(string sha,VendorMapSourceArchive archive,CancellationToken ct)=>{
            var bytes=await archive.ReadAsync(sha,ct);
            return bytes is null?Results.NotFound(new{error="当前Web后台未留存此原图；方案跨电脑转移时需同时保留原SMAP。"}):
                Results.File(bytes,"application/octet-stream",sha+".smap");
        });
        app.MapPost("/api/vehicles/connection", async (SetVehicleConnectionRequest request, DispatchPipe pipe, CancellationToken ct) =>
        {
            var settings=await pipe.ReadAsync<CommunicationSettingsSnapshot>("SetVehicleConnection",request,ct);
            return Results.Ok(new { settings!.Revision, Vehicles=settings.Saved.Agvs, ActiveVehicles=settings.Active.Agvs,
                settings.RestartRequired, settings.CanManage });
        });
        app.MapGet("/api/vehicles/configuration", async (DispatchPipe pipe, CancellationToken ct) =>
        {
            var settings = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct);
            return settings is null ? null : new { settings.Revision, Vehicles = settings.Saved.Agvs, ActiveVehicles=settings.Active.Agvs, settings.RestartRequired, settings.CanManage };
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
    public sealed record PrepareRuntimeMapRequest(string SourceSha256,
        Dictionary<string, AgvDispatch.Domain.Maps.MapNodeType> Roles, bool RobotCoordinatesConfirmed);
}
