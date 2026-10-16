using System.Collections.ObjectModel;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    private bool _snapToAlignment = true;
    public bool SnapToAlignment
    {
        get => _snapToAlignment;
        set { if (SetProperty(ref _snapToAlignment, value)) PreviewSnapGuides.Clear(); }
    }

    public ObservableCollection<MapCanvasGridLine> PreviewSnapGuides { get; } = [];
    private bool _snapToGeometry = true;
    public bool SnapToGeometry { get => _snapToGeometry; set { if (SetProperty(ref _snapToGeometry, value)) PreviewSnapGuides.Clear(); } }
    private readonly List<(double X, double Y, string EdgeId, string From, string To)> _geometrySnapTargets = [];

    private void RebuildGeometrySnapTargets()
    {
        _geometrySnapTargets.Clear();
        if (!ShowEdgeLayer) return;
        var nodes = Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var edge in Edges.Where(edge => IsMapLayerVisible(edge.LayerId)))
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to)) continue;
            var domain = edge.ToDomain(Nodes);
            if (!MapEdgeGeometry.TryValidate(domain, from.ToDomain(), to.ToDomain(), out _)) continue;
            foreach (var point in edge.ParseControlPoints()) _geometrySnapTargets.Add((point.X, point.Y, edge.Id, edge.From, edge.To));
            var middle = MapEdgeGeometry.PointAt(domain, from.ToDomain(), to.ToDomain(), .5);
            _geometrySnapTargets.Add((middle.X, middle.Y, edge.Id, edge.From, edge.To));
        }
    }

    private EditableMapNode? ResolvePlacementSnap(ref double x, ref double y, bool allowAlignment, IReadOnlySet<EditableMapNode>? excluded = null)
    {
        var pointX = x;
        var pointY = y;
        var visible = Nodes.Where(node => ShowNodeLayer && IsMapLayerVisible(node.LayerId) && excluded?.Contains(node) != true).ToArray();
        var nearest = SnapToExisting ? visible
            .Select(node => new { Node = node, Distance = Math.Sqrt(Math.Pow(node.X - pointX, 2) + Math.Pow(node.Y - pointY, 2)) })
            .Where(item => item.Distance <= SnapDistance).OrderBy(item => item.Distance)
            .ThenBy(item => item.Node.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Node : null;
        if (nearest is not null)
        {
            x = nearest.X; y = nearest.Y;
            return nearest;
        }
        if (SnapToGeometry && ShowEdgeLayer)
        {
            var excludedIds = excluded?.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidate = _geometrySnapTargets.Where(point => excludedIds is null ||
                    (!excludedIds.Contains(point.From) && !excludedIds.Contains(point.To)))
                .Select(point => new { Point = point, Distance = Math.Sqrt(Math.Pow(point.X - pointX, 2) + Math.Pow(point.Y - pointY, 2)) })
                .Where(item => item.Distance <= SnapDistance).OrderBy(item => item.Distance).FirstOrDefault();
            if (candidate is not null) { x = candidate.Point.X; y = candidate.Point.Y; return null; }
        }
        if (!allowAlignment || !SnapToAlignment) return null;
        var horizontal = visible.Where(node => Math.Abs(node.Y - pointY) <= SnapDistance)
            .OrderBy(node => Math.Abs(node.Y - pointY)).ThenBy(node => Math.Abs(node.X - pointX)).FirstOrDefault();
        var vertical = visible.Where(node => Math.Abs(node.X - pointX) <= SnapDistance)
            .OrderBy(node => Math.Abs(node.X - pointX)).ThenBy(node => Math.Abs(node.Y - pointY)).FirstOrDefault();
        if (horizontal is not null) y = horizontal.Y;
        if (vertical is not null) x = vertical.X;
        return null;
    }

    private void UpdateSnapGuides(double x, double y, IReadOnlySet<EditableMapNode>? excluded = null)
    {
        if (TryMapToCanvas(x, y) is not { } target) return;
        var visible = Nodes.Where(node => ShowNodeLayer && IsMapLayerVisible(node.LayerId) && excluded?.Contains(node) != true).ToArray();
        if (SnapToAlignment)
        {
            var vertical = visible.Where(node => Math.Abs(node.X - x) < 1e-8 && Math.Abs(node.Y - y) > 1e-8)
                .OrderBy(node => Math.Abs(node.Y - y)).FirstOrDefault();
            var horizontal = visible.Where(node => Math.Abs(node.Y - y) < 1e-8 && Math.Abs(node.X - x) > 1e-8)
                .OrderBy(node => Math.Abs(node.X - x)).FirstOrDefault();
            if (vertical is not null && TryMapToCanvas(vertical.X, vertical.Y) is { } v)
                PreviewSnapGuides.Add(new(target.X, v.Y, target.X, target.Y, "#D97706", 1));
            if (horizontal is not null && TryMapToCanvas(horizontal.X, horizontal.Y) is { } h)
                PreviewSnapGuides.Add(new(h.X, target.Y, target.X, target.Y, "#D97706", 1));
        }
        // A small cross marks the exact placement point, including node/grid snapping.
        PreviewSnapGuides.Add(new(target.X - 5, target.Y, target.X + 5, target.Y, "#D97706", 1.5));
        PreviewSnapGuides.Add(new(target.X, target.Y - 5, target.X, target.Y + 5, "#D97706", 1.5));
    }
}
