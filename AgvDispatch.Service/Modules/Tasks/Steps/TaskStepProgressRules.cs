using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>Describes current executor conditions, not the proposed unsigned PLC contract.</summary>
public static class TaskStepProgressRules
{
    public static IReadOnlyList<TaskPlcCondition> Observe(DispatchTaskStepType type, string station, string dumper, IPlcSignalCache cache)
    {
        var rules = type switch
        {
            DispatchTaskStepType.PickupFullBox => new[] { (station, "BoxPresent", false), (station, "FullBoxRemoved", true), (station, "EmptyBoxTaskAcknowledged", true) },
            DispatchTaskStepType.WaitForDumperReady => [(dumper, "Faulted", false), (dumper, "AgvAtWaitingPosition", true), (dumper, "DumperReady", true)],
            DispatchTaskStepType.DumpFullBox => [(dumper, "Faulted", false), (dumper, "AgvAtDumpingPosition", true), (dumper, "DumpingCompletedMayLeave", true)],
            DispatchTaskStepType.PlaceEmptyBox => [(station, "EmptyBoxPlaced", true), (station, "BoxPresent", true)],
            DispatchTaskStepType.CompletePlcHandshake => [(station, "EmptyBoxTaskAcknowledged", false), (station, "EmptyBoxPlaced", false), (station, "FullBoxRequestAcknowledged", false), (station, "FullBoxRemoved", false)],
            _ => Array.Empty<(string, string, bool)>()
        };
        return rules.Select(r =>
        {
            cache.TryGet(r.Item1, r.Item2, out var snapshot);
            return TaskSignalPredicate.Evaluate(r.Item1, r.Item2, r.Item3, snapshot,
                type == DispatchTaskStepType.CompletePlcHandshake ? "复位目标" : "执行条件");
        }).ToArray();
    }

    public static string Requirement(DispatchTaskStepType type) => type switch
    {
        DispatchTaskStepType.GoToPickup => "原车端取货任务成功；本步骤承载已配置取箱动作，第2步不重复取箱。",
        DispatchTaskStepType.PickupFullBox => "箱体为0；满箱拉走和空箱响应读回为1；执行器再清呼叫响应/满箱拉走。",
        DispatchTaskStepType.DeliverFullBoxToDumper => "原车端运输任务成功；下一步才发送等待位通知。",
        DispatchTaskStepType.WaitForDumperReady => "无故障、等待位通知读回为1、Ready为1；随后清等待位通知。",
        DispatchTaskStepType.EnterDumpingPosition => "原车端进入任务成功；下一步才发送倒料位通知。",
        DispatchTaskStepType.DumpFullBox => "无故障、倒料位通知读回为1、允许离开为1；随后清倒料位通知。",
        DispatchTaskStepType.ReturnEmptyBox => "原返程任务成功；本步骤承载已配置放箱动作，第8步不重复放箱。",
        DispatchTaskStepType.PlaceEmptyBox => "放到位通知读回为1，箱体检测为1。",
        DispatchTaskStepType.CompletePlcHandshake => "依次复位四个排屑机输出；当前实现写调用成功后结束步骤。",
        _ => "查看当前执行器步骤。"
    };

    public static string Boundary(DispatchTaskStepType type) => type switch
    {
        DispatchTaskStepType.PickupFullBox or DispatchTaskStepType.PlaceEmptyBox => "待电控核定：通知保持、PLC业务接收及复位责任；输出回读不是业务ACK。",
        DispatchTaskStepType.WaitForDumperReady => "待电控核定：Ready消费/复位和到位通知语义；设备独占及真实到位证据需联调。",
        DispatchTaskStepType.DumpFullBox => "当前按电平判断；本轮周期身份及上轮残留完成防护尚待契约与执行器完善。",
        DispatchTaskStepType.ReturnEmptyBox => "返程接受不等于实际离站；独立出区证据与设备/区域资源交接尚需核定。",
        DispatchTaskStepType.CompletePlcHandshake => "复位目标仅供核对；当前尚未独立等待全部下一轮基线闭合。",
        _ => "车端成功覆盖哪些机械动作、定位与现场一致性，仍以厂家确认和联调为准。"
    };
}
