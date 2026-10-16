using System.Collections.ObjectModel;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record MapEditHistoryRow(int Step, string Operation, DateTimeOffset Time, string State);

public sealed partial class MapManagementViewModel
{
    public const int MaximumEditHistory = 128;
    public ObservableCollection<MapEditHistoryRow> EditHistory { get; } = [];
    public string EditHistorySummary => $"可撤销 {_undo.Count} 步 · 可重做 {_redo.Count} 步 · 最多保留 {MaximumEditHistory} 步";

    private static string DescribeEdit(string method)
    {
        if (method == "图层设置") return method;
        if (method == "ConnectHorizontal") return "水平连接节点";
        if (method == "ConnectVertical") return "垂直连接节点";
        if (method.Contains("Drag")) return "拖动对象";
        if (method.Contains("Rotate")) return "旋转对象";
        if (method.Contains("Mirror")) return "镜像对象";
        if (method.Contains("Align") || method.Contains("Distribute")) return "对齐 / 分布";
        if (method.Contains("Remove") || method.Contains("Delete")) return "删除对象";
        if (method.Contains("Duplicate") || method.Contains("Paste")) return "复制对象";
        if (method.Contains("Split")) return "分割路段";
        if (method.Contains("Reverse")) return "反向路段";
        if (method.Contains("Move")) return "移动对象";
        if (method.Contains("Merge")) return "合并节点";
        if (method.Contains("Add") || method.Contains("MapClick")) return "新增 / 绘制";
        if (method.Contains("Boundary")) return "编辑资源边界";
        return "地图编辑";
    }

    private void PushHistory(MapEditorSnapshot before, string operation, bool merge)
    {
        var now = DateTimeOffset.Now;
        if (!merge || _redo.Count > 0 || !_undo.TryPeek(out var previous) || previous.Operation != operation ||
            now - previous.RecordedAt > TimeSpan.FromMilliseconds(200))
            _undo.Push(before with { Operation = operation, RecordedAt = now });
        if (_undo.Count <= MaximumEditHistory) return;
        var retained = _undo.Take(MaximumEditHistory).Reverse().ToArray();
        _undo.Clear(); foreach (var entry in retained) _undo.Push(entry);
    }

    private void RefreshEditHistory()
    {
        EditHistory.Clear();
        var step = _undo.Count;
        foreach (var entry in _undo) EditHistory.Add(new(step--, entry.Operation, entry.RecordedAt, "已执行"));
        step = _undo.Count + 1;
        foreach (var entry in _redo) EditHistory.Add(new(step++, entry.Operation, entry.RecordedAt, "已撤销"));
        RaisePropertyChanged(nameof(EditHistorySummary));
    }
}
