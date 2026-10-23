using System.Text.Json;

namespace PlcHandshakeLab;

public sealed record SignalComparison(string Name, string Expected, string Actual, string Status);

/// <summary>Read-only projections: filtering and inspection never advance the lab or write its DB.</summary>
public static class LabPresentation
{
    public static string Expected(int stage, int signal) => Sequence.Stages[stage].Pattern[signal] == '?' ? "0/1" : Sequence.Stages[stage].Pattern[signal].ToString();

    public static SignalComparison[] Compare(int stage, PointState[]? points, bool communicationFresh, DateTimeOffset now) =>
        Sequence.Signals.Select((signal, index) =>
        {
            var point = points?.SingleOrDefault(p => p.Mapping.Signal == index);
            if (point is null) return new SignalComparison(signal.Name, Expected(stage, index), "—", "尚无会话数据");
            var fresh = communicationFresh && CommunicationStatus.PointFresh(point, now);
            return new SignalComparison(signal.Name, Expected(stage, index), (point.Value ? "1" : "0") + (fresh ? "" : "·旧"),
                (Sequence.Matches(stage, index, point.Value) ? "符合" : "差异") + (fresh ? " · 已交换" : " · 待核对"));
        }).ToArray();

    // Filter indices correspond to the four visible choices. Errors also remain visible in their
    // business/communication context; a filter affects display only, never the journal or exports.
    public static bool MatchesEvent(NetworkEvent item, int filter)
    {
        var communication = item.Kind.StartsWith("S7 ", StringComparison.Ordinal) || item.Kind is "监听" or "连接" or "断开连接" or "拒绝连接" or "协议/连接错误" or "通信恢复" or "通信中断" or "停止";
        var anomaly = item.Kind is "拒绝连接" or "协议/连接错误" or "通信中断" or "拒绝写入" or "自动恢复门禁" ||
            item.Point.Contains("故障", StringComparison.Ordinal) && item.After == "1" ||
            item.Point.Contains("下游在线", StringComparison.Ordinal) && item.After == "0" || FailedStage(item);
        return filter switch { 1 => !communication, 2 => communication, 3 => anomaly, _ => true };
    }

    private static bool FailedStage(NetworkEvent item)
    {
        if (item.Kind != "阶段测试") return false;
        var start = item.Detail.IndexOf('{');
        if (start < 0) return false;
        try { return JsonSerializer.Deserialize<StageTestResult>(item.Detail[start..])?.Outcome == "失败"; }
        catch (JsonException) { return false; }
    }
}
