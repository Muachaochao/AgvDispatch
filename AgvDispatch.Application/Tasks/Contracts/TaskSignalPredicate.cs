using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

/// <summary>Shared with the executor. Preserve its existing Good + BOOL semantics;
/// freshness classification is owned by the PLC acquisition/cache, not invented by the UI.</summary>
public static class TaskSignalPredicate
{
    public static bool TryRead(PlcSignalSnapshot? snapshot, out bool value)
    {
        value = false;
        if (snapshot?.Quality != SignalQuality.Good || snapshot.Value is not bool current) return false;
        value = current;
        return true;
    }

    public static TaskPlcCondition EvaluateFresh(string station,string variable,bool expected,PlcSignalSnapshot? snapshot,DateTimeOffset now,int maxAgeSeconds=5,string role="执行条件（新契约）")
    {
        var result=Evaluate(station,variable,expected,snapshot,role);
        if(snapshot?.Enabled!=true||snapshot.UpdatedAt is not {} time||time>now.AddSeconds(1)||now-time>TimeSpan.FromSeconds(Math.Clamp(maxAgeSeconds,1,60)))
            return result with {Satisfied=false,Result="Unknown",Reason="点位未启用、缺少采集时间或观察已过期/时间异常；不能自动推进。"};
        return result;
    }

    private static string SignalName(string variable) => variable switch
    {
        "BoxPresent" => "箱体检测", "FullBoxRequestAcknowledged" => "满箱呼叫响应",
        "FullBoxRemoved" => "满箱拉走", "EmptyBoxTaskAcknowledged" => "空箱任务响应",
        "EmptyBoxPlaced" => "空箱放到位", "Faulted" => "设备故障",
        "AgvAtWaitingPosition" => "AGV到等待位", "DumperReady" => "倒料机准备OK",
        "AgvAtDumpingPosition" => "AGV到倒料位", "DumpingCompletedMayLeave" => "倒料完成允许离开",
        _ => variable
    };

    public static TaskPlcCondition Evaluate(string station, string variable, bool expected, PlcSignalSnapshot? snapshot,
        string role = "执行条件")
    {
        var known = TryRead(snapshot, out var actual);
        var pass = known && actual == expected;
        return new(station, variable, snapshot?.Address ?? "未配置", expected, snapshot?.Value,
            snapshot?.Quality ?? SignalQuality.Unknown, pass)
        {
            RuleId = $"PLC.{variable}.{(expected ? 1 : 0)}", Name = snapshot?.ChineseName ?? SignalName(variable),
            Result = !known ? "Unknown" : pass ? "Pass" : "Wait",
            Reason = !known ? $"信号缺失或质量无效（{snapshot?.Quality.ToString() ?? "Unknown"}），不能按0处理。"
                : pass ? role == "复位目标" ? "当前值符合复位目标；不代表PLC业务周期已闭合。" : "当前值满足；不单独证明机械动作或PLC业务接收。"
                : $"期望{(expected ? 1 : 0)}，实际{(actual ? 1 : 0)}。",
            ObservedAt = snapshot?.UpdatedAt, SignalVersion = snapshot?.Version, Role = role
        };
    }
}
