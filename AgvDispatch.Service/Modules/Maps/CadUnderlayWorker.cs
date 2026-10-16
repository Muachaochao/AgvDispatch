using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

public sealed class CadUnderlayWorker(
    ILogger<CadUnderlayWorker> logger,
    ISqlSugarClient database,
    ICadUnderlayJobQueue queue,
    ICadUnderlayService renderer,
    MapAssetStorageOptions storage) : IsolatedBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedJobsAsync(stoppingToken);
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            await ProcessAsync(job, stoppingToken);
        }
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<MapProjectEntity>()
            .Where(item => item.UnderlayStatus == (int)CadUnderlayJobStatus.Pending ||
                           item.UnderlayStatus == (int)CadUnderlayJobStatus.Processing)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var layers = DeserializeLayers(row.UnderlayIncludedLayersJson);
            await queue.QueueAsync(new CadUnderlayJob(row.MapId, row.BackgroundFilePath, layers,
                row.UnderlayWidth ?? 4096, row.UnderlayHeight ?? 2608), cancellationToken);
        }
    }

    private async Task ProcessAsync(CadUnderlayJob job, CancellationToken cancellationToken)
    {
        try
        {
            await SetStateAsync(job.MapId, CadUnderlayJobStatus.Processing, "正在解析CAD并生成PNG/SVG底图。", cancellationToken);
            var outputDirectory = Path.Combine(storage.CachePath, SafePathSegment(job.MapId));
            var rendered = await renderer.RenderAsync(
                new CadUnderlayRenderRequest(job.SourcePath, outputDirectory, job.IncludedLayers, job.Width, job.Height),
                cancellationToken);
            var included = new HashSet<string>(rendered.IncludedLayers, StringComparer.OrdinalIgnoreCase);
            var layers = rendered.AvailableLayers
                .Select(name => new CadLayerSnapshot(name, included.Contains(name)))
                .ToArray();
            var message = $"CAD底图生成完成：{job.Width}×{job.Height}，图层{layers.Length}个，显示{layers.Count(item => item.Included)}个。";
            if (rendered.Warnings.Count > 0) message += " " + string.Join(" ", rendered.Warnings);
            var layersJson = JsonSerializer.Serialize(layers);
            var viewBoxJson = rendered.ViewBox == null ? null : JsonSerializer.Serialize(rendered.ViewBox);
            await database.Updateable<MapProjectEntity>()
                .SetColumns(item => new MapProjectEntity
                {
                    PreviewPngPath = rendered.PreviewPngPath,
                    PreviewSvgPath = rendered.PreviewSvgPath,
                    CadLayersJson = layersJson,
                    CadViewBoxJson = viewBoxJson,
                    UnderlayStatus = (int)CadUnderlayJobStatus.Ready,
                    UnderlayMessage = message,
                    UnderlayGeneratedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                })
                .Where(item => item.MapId == job.MapId)
                .ExecuteCommandAsync(cancellationToken);
            logger.LogInformation("{Message} MapId={MapId}", message, job.MapId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"CAD底图生成失败：{exception.Message}";
            await SetStateAsync(job.MapId, CadUnderlayJobStatus.Failed, message, CancellationToken.None);
            logger.LogError(exception, "CAD底图生成失败。MapId={MapId}", job.MapId);
        }
    }

    private Task<int> SetStateAsync(
        string mapId,
        CadUnderlayJobStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        var storedMessage = message.Length <= 2000 ? message : message.Substring(0, 2000);
        return database.Updateable<MapProjectEntity>()
            .SetColumns(item => new MapProjectEntity
            {
                UnderlayStatus = (int)status,
                UnderlayMessage = storedMessage,
                UpdatedAt = DateTime.Now
            })
            .Where(item => item.MapId == mapId)
            .ExecuteCommandAsync(cancellationToken);
    }

    private static string[] DeserializeLayers(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    private static string SafePathSegment(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
