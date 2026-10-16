using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Maps;

/// <summary>Read-only display cache: never modifies map versions, calibration or dispatch state.</summary>
public sealed class CadDetailService(ISqlSugarClient database, ICadUnderlayService renderer, MapAssetStorageOptions storage) : ICadDetailService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CadDetailResult> _cache = [];
    private readonly LinkedList<string> _lru = [];
    private readonly object _requestLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _latestRequests = [];

    public async Task<CadDetailResult> GetAsync(CadDetailRequest request, CancellationToken cancellationToken)
    {
        if (request.Width < 320 || request.Height < 240 || request.Width > 2048 || request.Height > 2048)
            throw new ArgumentException("局部高清分辨率上限为2048×2048。");
        var supersession = new CancellationTokenSource();
        lock (_requestLock)
        {
            if (_latestRequests.TryGetValue(request.MapId, out var older)) older.Cancel();
            _latestRequests[request.MapId] = supersession;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, supersession.Token);
        cancellationToken = linked.Token;
        try
        {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var detailDirectory = Path.Combine(storage.CachePath, "cad-detail");
            if (Directory.Exists(detailDirectory))
            {
                // Only our generated GUID PNGs in the dedicated directory, never source CAD or base previews.
                var files = Directory.EnumerateFiles(detailDirectory, "underlay-*.png")
                    .Where(p => Guid.TryParseExact(Path.GetFileNameWithoutExtension(p)[9..], "N", out _))
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
                foreach (var old in files.Skip(32)) TryDelete(old);
            }
            var project = await database.Queryable<MapProjectEntity>().Where(p => p.MapId == request.MapId).FirstAsync(cancellationToken);
            if (project is null || project.UnderlayStatus != (int)CadUnderlayJobStatus.Ready ||
                project.PreviewPngPath != request.BasePreviewPath || project.CadViewBoxJson is null)
                throw new InvalidOperationException("底图已变化或未就绪，请刷新地图。");
            var frame = JsonSerializer.Deserialize<CadViewBoxSnapshot>(project.CadViewBoxJson)!;
            var r = request.Region;
            if (!double.IsFinite(r.MinX) || !double.IsFinite(r.MinY) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height) ||
                r.Width <= 0 || r.Height <= 0 || r.MinX < frame.MinX - 1e-5 || r.MinY < frame.MinY - 1e-5 ||
                r.MinX + r.Width > frame.MinX + frame.Width + 1e-5 || r.MinY + r.Height > frame.MinY + frame.Height + 1e-5)
                throw new ArgumentException("局部范围必须位于当前CAD底图内。");
            var source = new FileInfo(project.BackgroundFilePath);
            var layers = JsonSerializer.Deserialize<string[]>(project.UnderlayIncludedLayersJson) ?? [];
            var key = JsonSerializer.Serialize(new { request, source.Length, source.LastWriteTimeUtc, layers });
            if (_cache.TryGetValue(key, out var cached) && File.Exists(cached.PreviewPngPath))
            { _lru.Remove(key); _lru.AddLast(key); return cached; }
            var result = await renderer.RenderAsync(new(project.BackgroundFilePath,
                detailDirectory, layers, request.Width, request.Height, r), cancellationToken);
            var current = await database.Queryable<MapProjectEntity>().Where(p => p.MapId == request.MapId).FirstAsync(cancellationToken);
            if (current?.PreviewPngPath != project.PreviewPngPath || current.UnderlayStatus != (int)CadUnderlayJobStatus.Ready ||
                current.BackgroundFilePath != project.BackgroundFilePath || current.UnderlayIncludedLayersJson != project.UnderlayIncludedLayersJson)
            {
                TryDelete(result.PreviewPngPath);
                throw new InvalidOperationException("生成期间底图已变化，局部结果已丢弃。");
            }
            var detail = new CadDetailResult(result.PreviewPngPath, result.ViewBox!);
            _cache[key] = detail; _lru.Remove(key); _lru.AddLast(key);
            while (_lru.Count > 24)
            {
                var oldest = _lru.First!.Value; _lru.RemoveFirst();
                if (_cache.Remove(oldest, out var expired)) TryDelete(expired.PreviewPngPath);
            }
            return detail;
        }
        finally { _gate.Release(); }
        }
        finally
        {
            lock (_requestLock)
            {
                if (_latestRequests.TryGetValue(request.MapId, out var current) && ReferenceEquals(current, supersession))
                    _latestRequests.Remove(request.MapId);
                supersession.Dispose();
            }
        }
    }

    private static void TryDelete(string generatedPath)
    {
        try { File.Delete(generatedPath); }
        catch (IOException) { } // A reader can hold a file momentarily; disk pruning retries on the next request.
        catch (UnauthorizedAccessException) { }
    }
}
