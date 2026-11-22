using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskHistoryExportService
{
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private static readonly Regex CacheName = new("^任务处置历史-([a-f0-9]{32})\\.zip(\\.tmp)?$", RegexOptions.CultureInvariant);
    private static readonly Regex StagingName = new("^([a-f0-9]{32})\\.staging$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StagingFiles = ["history.csv", "evidence.jsonl", "report.html", "manifest.json", "README.txt"];
    public async Task<TaskHistoryExportCache> GetCacheAsync(TrafficUserContext user, TaskHistoryExportCacheRequest request, CancellationToken ct)
    {
        DemandView(user); ValidRetention(request.RetentionDays);
        await _cacheGate.WaitAsync(ct);
        try
        {
            var files = ScanCache(request.RetentionDays, ct).ToArray();
            return new(files.OrderByDescending(f => f.Item.UpdatedAt).Take(200).Select(f => f.Item).ToArray(), files.Length,
                files.Sum(f => f.Item.SizeBytes), files.Count(f => f.Item.CanClean), request.RetentionDays);
        }
        finally { _cacheGate.Release(); }
    }
    public async Task<TaskHistoryExportCleanupResult> CleanupAsync(TrafficUserContext user, TaskHistoryExportCacheRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.Administer)) throw new UnauthorizedAccessException("缓存清理需要本地管理权限。");
        ValidRetention(request.RetentionDays); await _cacheGate.WaitAsync(ct);
        try
        {
            var deleted = 0; var failed = 0; long bytes = 0;
            foreach (var entry in ScanCache(request.RetentionDays, ct).Where(e => e.Item.CanClean).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Recheck the directory, link flags and contents immediately before deletion.
                    ValidateCacheRoot();
                    var current = InspectCacheEntry(entry.Path, request.RetentionDays);
                    if (current == null || !current.Value.Item.CanClean) continue;
                    if (Directory.Exists(current.Value.Path))
                    {
                        foreach (var name in StagingFiles)
                        {
                            var file = Path.Combine(current.Value.Path, name);
                            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                        }
                        Directory.Delete(current.Value.Path, false);
                    }
                    else File.Delete(current.Value.Path);
                    deleted++; bytes += current.Value.Item.SizeBytes;
                }
                catch (IOException) { failed++; }
                catch (UnauthorizedAccessException) { failed++; }
            }
            return new(deleted, bytes, failed, $"已清理 {deleted} 项过期缓存，释放 {bytes:N0} 字节；{failed} 项因占用或权限未清理。已保存到其他位置的交接文件保持不变。");
        }
        finally { _cacheGate.Release(); }
    }
    private static void ValidRetention(int days) { if (days < 1 || days > 3650) throw new ArgumentException("缓存保留期限为1~3650天。"); }
    private void ValidateCacheRoot()
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(CacheRoot)); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("缓存目录包含目录链接，已停止清理，请检查缓存位置。");
    }
    private IEnumerable<(string Path, TaskHistoryExportCacheItem Item)> ScanCache(int days, CancellationToken ct)
    {
        ValidateCacheRoot(); if (!Directory.Exists(CacheRoot)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(CacheRoot))
        {
            ct.ThrowIfCancellationRequested();
            (string Path, TaskHistoryExportCacheItem Item)? entry;
            try { entry = InspectCacheEntry(path, days); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (entry != null) yield return entry.Value;
        }
    }
    private (string Path, TaskHistoryExportCacheItem Item)? InspectCacheEntry(string path, int days)
    {
        var absolute = Path.GetFullPath(path);
        if (!Path.GetDirectoryName(absolute)!.Equals(Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileName(absolute); var attributes = File.GetAttributes(absolute);
        if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
        string id, kind; long size; DateTime updated;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            var match = StagingName.Match(name); if (!match.Success) return null;
            // Owned staging directories contain only the five known files and no child directories.
            var contents = Directory.EnumerateFileSystemEntries(absolute).ToArray();
            if (contents.Any(p => !StagingFiles.Contains(Path.GetFileName(p)) || (File.GetAttributes(p) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)) return null;
            id = match.Groups[1].Value; kind = "中断暂存目录";
            size = contents.Sum(p => new FileInfo(p).Length);
            updated = new[] { Directory.GetCreationTimeUtc(absolute), Directory.GetLastWriteTimeUtc(absolute) }
                .Concat(contents.SelectMany(p => new[] { File.GetCreationTimeUtc(p), File.GetLastWriteTimeUtc(p) })).Max();
        }
        else
        {
            var match = CacheName.Match(name); if (!match.Success) return null;
            id = match.Groups[1].Value; kind = match.Groups[2].Success ? "未完成临时包" : "导出包";
            var info = new FileInfo(absolute); size = info.Length; updated = new[] { info.CreationTimeUtc, info.LastWriteTimeUtc }.Max();
        }
        var active = _jobs.TryGetValue(id, out var job) && !job.Progress.IsTerminal;
        return (absolute, new(name, kind, new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)).ToLocalTime(), size,
            !active && updated < AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().UtcDateTime.AddDays(-days)));
    }
}
