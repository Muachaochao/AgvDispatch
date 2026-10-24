using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public sealed record DisplayPoint(double X, double Y);
public sealed record DisplayArea(string Id, string Name, string Color, DisplayPoint[] Boundary, string[] StationIds,
    bool ShowName = true, bool ShowBoundary = true);
public sealed record DisplayAreaDocument(string MapId, string BasisHash, string Revision, DateTimeOffset SavedAt, DisplayArea[] Areas);
public sealed record DisplayAreaSave(string ExpectedRevision, string BasisHash, DisplayArea[] Areas);

// Display-only metadata. No dispatcher, navigation, traffic resources or PLC writes.
public sealed class DisplayAreaStore(string root)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string FilePath(string mapId) => Path.Combine(root, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mapId))) + ".json");
    public async Task<DisplayAreaDocument?> ReadAsync(string mapId, CancellationToken ct)
    {
        var path = FilePath(mapId);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("展示分区文件过大。");
        var doc = JsonSerializer.Deserialize<DisplayAreaDocument>(bytes, WebJson.Options) ?? throw new InvalidDataException("展示分区文件无效。");
        if (doc.MapId != mapId) throw new InvalidDataException("展示分区地图归属无效。");
        return doc;
    }
    public static void Validate(DisplayArea[] areas, IEnumerable<string> stationIds)
    {
        if (areas is null || areas.Length > 200) throw new ArgumentException("最多保存200个展示区域。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stations = stationIds.ToHashSet(StringComparer.Ordinal);
        foreach (var a in areas)
        {
            if (a is null || !Guid.TryParse(a.Id, out _) || !ids.Add(a.Id) || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 80 ||
                a.Color is null || !Regex.IsMatch(a.Color, "^#[0-9a-fA-F]{6}$") || a.Boundary is null || a.Boundary.Length is < 3 or > 100 ||
                a.Boundary.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1e7 || Math.Abs(p.Y) > 1e7) ||
                a.StationIds is null || a.StationIds.Length > 5000 || a.StationIds.Distinct().Count() != a.StationIds.Length || a.StationIds.Any(id => !stations.Contains(id)))
                throw new ArgumentException("区域名称、颜色、边界或站点归属无效，请重新核对。");
            var polygon = a.Boundary;
            double signed = 0;
            for (var i = 0; i < polygon.Length; i++)
            {
                var p = polygon[i]; var q = polygon[(i + 1) % polygon.Length];
                if (Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y) < 1e-8) throw new ArgumentException("区域边界存在重复顶点。");
                signed += p.X * q.Y - q.X * p.Y;
                for (var j = i + 1; j < polygon.Length; j++)
                {
                    if (j == i + 1 || (i == 0 && j == polygon.Length - 1)) continue;
                    if (Intersects(p, q, polygon[j], polygon[(j + 1) % polygon.Length])) throw new ArgumentException("区域边界不能自相交。");
                }
            }
            if (Math.Abs(signed) < 1e-6) throw new ArgumentException("区域边界面积过小。");
        }
    }
    private static bool Intersects(DisplayPoint a, DisplayPoint b, DisplayPoint c, DisplayPoint d)
    {
        static double Cross(DisplayPoint p, DisplayPoint q, DisplayPoint r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        return Math.Max(a.X, b.X) >= Math.Min(c.X, d.X) && Math.Max(c.X, d.X) >= Math.Min(a.X, b.X) &&
            Math.Max(a.Y, b.Y) >= Math.Min(c.Y, d.Y) && Math.Max(c.Y, d.Y) >= Math.Min(a.Y, b.Y) &&
            Cross(a, b, c) * Cross(a, b, d) <= 0 && Cross(c, d, a) * Cross(c, d, b) <= 0;
    }
    public async Task<DisplayAreaDocument> SaveAsync(string mapId, DisplayAreaSave request, IEnumerable<string> stationIds, CancellationToken ct)
    {
        Validate(request.Areas, stationIds);
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(root);
            await using var fileLock = new FileStream(Path.Combine(root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var old = await ReadAsync(mapId, ct);
            if ((old?.Revision ?? "") != request.ExpectedRevision) throw new ProjectConflictException("展示分区已被其他页面修改，请先重新读取；本页修改仍保留。");
            var doc = new DisplayAreaDocument(mapId, request.BasisHash, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, request.Areas);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, WebJson.Options);
            if (bytes.Length > 2 * 1024 * 1024) throw new ArgumentException("展示分区超出2MiB预算。");
            var path = FilePath(mapId); var temp = path + ".tmp";
            try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return doc;
        }
        finally { gate.Release(); }
    }
}

public static class DisplayAreasApi
{
    private static async Task<WebMap> MapAsync(string id, DispatchPipe pipe, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("请选择已保存的后台地图。");
        var active = await pipe.ReadAsync<TrafficMapSnapshot>(TrafficControlOperations.GetTrafficMap, new TrafficMapQuery(), ct);
        if (active?.MapId == id && active.Definition is not null) return SnapshotReader.ProjectMap(active)!;
        var workspace = await pipe.ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace, new { MapId = id }, ct)
            ?? throw new KeyNotFoundException("后台地图不存在。");
        var version = workspace.Published ?? workspace.Draft;
        return SnapshotReader.ProjectMap(new TrafficMapSnapshot(DateTimeOffset.UtcNow, id, version.VersionId, version.Definition, []))!;
    }
    public static string Basis(WebMap map) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { map.CoordinateSystem, map.Unit, map.Nodes }, WebJson.Options)));
    public static void MapDisplayAreasApi(this WebApplication app)
    {
        app.MapGet("/api/maps/{id}/display-areas", async (string id, DisplayAreaStore store, DispatchPipe pipe, CancellationToken ct) =>
        {
            var map = await MapAsync(id, pipe, ct); var basis = Basis(map); var doc = await store.ReadAsync(id, ct);
            return new { map, basisHash = basis, document = doc, applicable = doc is null || doc.BasisHash == basis };
        });
        app.MapPost("/api/maps/{id}/display-areas", async (string id, DisplayAreaSave request, DisplayAreaStore store, DispatchPipe pipe, CancellationToken ct) =>
        {
            var map = await MapAsync(id, pipe, ct);
            if (map.Unit != "m" || request.BasisHash != Basis(map)) throw new ProjectConflictException("地图站点或坐标已变化，请重新读取后核对区域。");
            var nodes = map.Nodes.Select(n => JsonSerializer.SerializeToElement(n, WebJson.Options).GetProperty("id").GetString()!);
            return await store.SaveAsync(id, request, nodes, ct);
        });
    }
}
