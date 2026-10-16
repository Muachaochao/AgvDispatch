using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Infrastructure.Maps;

public sealed record CadCandidateSource(string EdgeId, string Handle, string Layer);
public sealed record CadNavigationCandidatePreview(MapDefinition Definition, IReadOnlyList<CadCandidateSource> Sources,
    IReadOnlyDictionary<string, int> SkippedTypes, int SkippedInvalid, int SkippedDuplicate, IReadOnlyList<string> ReviewItems);

/// <summary>Extracts explicitly selected planar LINE entities as disabled editing candidates.</summary>
public sealed class CadNavigationCandidateBuilder
{
    public CadNavigationCandidatePreview Build(CadDocument document, IReadOnlySet<string> selectedLayers, double metresPerUnit, string source)
    {
        if (selectedLayers.Count == 0) throw new ArgumentException("必须明确选择候选导航图层。");
        if (!double.IsFinite(metresPerUnit) || metresPerUnit <= 0) throw new ArgumentOutOfRangeException(nameof(metresPerUnit));
        var known = document.Layers.Select(layer => layer.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedLayers.Any(layer => !known.Contains(layer))) throw new ArgumentException("指定图层不存在，请先列出图层。");
        var nodes = new Dictionary<(double X, double Y), MapNode>();
        var edges = new List<MapEdge>(); var sources = new List<CadCandidateSource>();
        var skipped = new Dictionary<string, int>(); var invalid = 0; var duplicate = 0;
        var pairs = new HashSet<(string, string)>();
        MapNode GetNode(double x, double y)
        {
            if (nodes.TryGetValue((x, y), out var found)) return found;
            var node = new MapNode($"CAD-N{nodes.Count + 1:D5}", x, y, MapNodeType.Waypoint, false, []) { LayerId = "cad-candidates" };
            nodes.Add((x, y), node); return node;
        }
        foreach (var entity in document.ModelSpace.Entities)
        {
            if (!selectedLayers.Contains(entity.Layer.Name) || !entity.Layer.IsOn || entity.Layer.Flags.HasFlag(LayerFlags.Frozen)) continue;
            if (entity is not Line line)
            { var type = entity.GetType().Name; skipped[type] = skipped.GetValueOrDefault(type) + 1; continue; }
            var ax = line.StartPoint.X * metresPerUnit; var ay = line.StartPoint.Y * metresPerUnit;
            var bx = line.EndPoint.X * metresPerUnit; var by = line.EndPoint.Y * metresPerUnit;
            var length = Math.Sqrt(Math.Pow(bx - ax, 2) + Math.Pow(by - ay, 2));
            if (!double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(bx) || !double.IsFinite(by) ||
                !double.IsFinite(line.StartPoint.Z) || !double.IsFinite(line.EndPoint.Z) ||
                !double.IsFinite(length) || length <= 1e-6 || Math.Abs(line.StartPoint.Z) > 1e-8 || Math.Abs(line.EndPoint.Z) > 1e-8)
            { invalid++; continue; }
            var from = GetNode(ax, ay); var to = GetNode(bx, by);
            var pair = string.CompareOrdinal(from.Id, to.Id) < 0 ? (from.Id, to.Id) : (to.Id, from.Id);
            if (!pairs.Add(pair)) { duplicate++; continue; }
            var edge = new MapEdge($"CAD-E{edges.Count + 1:D5}", from.Id, to.Id, length, .3, false, false, []) { LayerId = "cad-candidates" };
            edges.Add(edge); sources.Add(new(edge.Id, entity.Handle.ToString("X"), entity.Layer.Name));
        }
        var definition = new MapDefinition("CAD导航候选（待核对）", source, "LocalCartesian", "m", nodes.Values.ToArray(), edges, [])
        { Layers = [new("cad-candidates", "CAD导航候选") { Color = "#D97706" }] };
        return new(definition, sources, skipped, invalid, duplicate,
            ["节点与路段默认禁用。人工核对后再设置通行方向、限速和启用状态。",
             "只提取所选可见图层的平面直线；块、折线、圆弧和样条暂不转换。",
             "只连接坐标完全相同的端点，不合并近邻点，不把相交线自动切分成路口。",
             "保留原点并按指定比例换算为米；已有标定必须另外核对。",
             "业务站点厂家编号、交通资源、车辆可达性均未推断。"]);
    }
}
