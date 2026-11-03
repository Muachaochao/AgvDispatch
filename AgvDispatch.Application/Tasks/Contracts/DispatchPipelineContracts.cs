namespace AgvDispatch.Application.Abstractions;

public sealed record DispatchPipelineLayerSnapshot(string Key, int Number, string Name, string State,
    string Summary, int? ItemCount, DateTimeOffset UpdatedAt);

/// <summary>Observations of real stage work; reporting is never an execution permission.</summary>
public interface IDispatchPipelineMonitor
{
    void ReportLayer(string key, string state, string summary, int? itemCount = null);
}

public static class DispatchPipelineLayers
{
    public static IReadOnlyList<DispatchPipelineLayerSnapshot> Initial() =>
    [
        Layer("queue", 1, "PLC 呼叫队列"), Layer("candidates", 2, "候选筛选"),
        Layer("matching", 3, "滚动全局匹配"), Layer("traffic", 4, "交通路权"),
        Layer("safety", 5, "安全复核"), Layer("execution", 6, "执行与恢复")
    ];
    private static DispatchPipelineLayerSnapshot Layer(string key, int number, string name) =>
        new(key, number, name, "Idle", "尚未观察该层的实际工作。", null, DateTimeOffset.MinValue);
}
