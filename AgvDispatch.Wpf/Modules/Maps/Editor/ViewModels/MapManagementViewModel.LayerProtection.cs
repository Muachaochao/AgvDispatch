using System.Text.Json;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    private bool IsLayerSelectable(string id) => MapLayers.FirstOrDefault(layer => layer.Id == id)?.IsAllowSelect ?? true;
    private bool IsLayerLocked(string id) => MapLayers.FirstOrDefault(layer => layer.Id == id)?.IsLocked ?? false;
    private bool IsNodeProtected(EditableMapNode node) => IsLayerLocked(node.LayerId) ||
        Edges.Any(edge => IsLayerLocked(edge.LayerId) && (edge.From == node.Id || edge.To == node.Id));

    private DelegateCommand CreateMapEditCommand(Action action, Func<bool>? enabled = null) =>
        new(() => RunMapEdit(action), enabled ?? CanManage);

    private void RunMapEdit(Action action)
    {
        if (!CanManage()) return;
        var hiddenTarget=CurrentLayer is { } target&&(!IsMapLayerVisible(target.Id)||!IsLayerSelectable(target.Id));
        if (!MapLayers.Any(layer => layer.IsLocked)&&!hiddenTarget) { action(); return; }
        var before = CaptureSnapshot();
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        action();
        var after=CaptureSnapshot();var targetId=CurrentLayer?.Id;
        var hiddenCreated=hiddenTarget&&(after.Nodes.Any(n=>n.LayerId==targetId&&!before.Nodes.Any(b=>b.Id==n.Id))||after.Edges.Any(n=>n.LayerId==targetId&&!before.Edges.Any(b=>b.Id==n.Id))||after.Resources.Any(n=>n.LayerId==targetId&&!before.Resources.Any(b=>b.Id==n.Id)));
        if (!LockedObjectsChanged(before, after)&&!hiddenCreated) return;
        _undo.Clear(); foreach (var entry in undo.Reverse()) _undo.Push(entry);
        _redo.Clear(); foreach (var entry in redo.Reverse()) _redo.Push(entry);
        RestoreSnapshot(before);
        if(hiddenCreated){StatusMessage="当前绘图层隐藏或不允许选择，已取消新增对象；请先显示该层并允许选择。";return;}
        StatusMessage = "操作涉及锁定图层或其路段端点，已取消。请先在图层面板解除锁定。";
    }

    private static bool LockedObjectsChanged(MapEditorSnapshot before, MapEditorSnapshot after)
    {
        var locked = before.Layers.Where(layer => layer.IsLocked).Select(layer => layer.Id).ToHashSet();
        if (locked.Count == 0) return false;
        var endpointIds = before.Edges.Where(edge => locked.Contains(edge.LayerId))
            .SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Protected(MapEditorSnapshot snapshot) => JsonSerializer.Serialize(new
        {
            Nodes = snapshot.Nodes.Where(node => locked.Contains(node.LayerId) || endpointIds.Contains(node.Id)).OrderBy(node => node.Id),
            Edges = snapshot.Edges.Where(edge => locked.Contains(edge.LayerId)).OrderBy(edge => edge.Id),
            Resources = snapshot.Resources.Where(resource => locked.Contains(resource.LayerId)).OrderBy(resource => resource.Id)
        });
        return Protected(before) != Protected(after);
    }

    private void ApplyLayerStyles()
    {
        var layers = MapLayers.ToDictionary(layer => layer.Id);
        var nodeLayers = Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First().LayerId);
        var edgeLayers = Edges.GroupBy(edge => edge.Id).ToDictionary(group => group.Key, group => group.First().LayerId);
        for (var i = 0; i < PreviewNodes.Count; i++)
        {
            var node = PreviewNodes[i];
            if (nodeLayers.TryGetValue(node.Id, out var id) && layers.TryGetValue(id, out var layer))
                PreviewNodes[i] = node with { Fill = layer.Color ?? node.Fill, Opacity = layer.Alpha, CanSelect = layer.IsAllowSelect && layer.Alpha > 0 };
        }
        for (var i = 0; i < PreviewEdges.Count; i++)
        {
            var edge = PreviewEdges[i];
            if (edgeLayers.TryGetValue(edge.Id, out var id) && layers.TryGetValue(id, out var layer))
                PreviewEdges[i] = edge with { Stroke = edge.Thickness >= 6 ? edge.Stroke : layer.Color ?? edge.Stroke,
                    Thickness = edge.Thickness >= 6 ? edge.Thickness : layer.StrokeWidth, Opacity = layer.Alpha, CanSelect = layer.IsAllowSelect && layer.Alpha > 0 };
        }
        var resourceLayers = Resources.GroupBy(resource => resource.Id).ToDictionary(group => group.Key, group => group.First().LayerId);
        for (var i = 0; i < PreviewResourceBoundaries.Count; i++)
        {
            var boundary = PreviewResourceBoundaries[i];
            if (resourceLayers.TryGetValue(boundary.Id, out var id) && layers.TryGetValue(id, out var layer))
                PreviewResourceBoundaries[i] = boundary with { Stroke = boundary.Id == SelectedResource?.Id ? boundary.Stroke : layer.Color ?? boundary.Stroke,
                    Thickness = Math.Max(boundary.Thickness, layer.StrokeWidth), Opacity = layer.Alpha };
        }
        ApplyDisplayReadability();
    }
}
