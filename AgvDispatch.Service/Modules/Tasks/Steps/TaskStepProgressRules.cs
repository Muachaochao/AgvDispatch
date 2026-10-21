using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>Legacy observation rules and explicit descriptions of the commissioned 28-stage contract.</summary>
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

    public static string Requirement28(DispatchTaskStepType type) => type switch
    {
        DispatchTaskStepType.GoToPickup => "28阶段4～6：受理应答1且PLC已撤呼叫0后才开始取箱运输；原车端取货任务及配置取箱动作成功，下一步另核实实际无箱，不重复发取箱动作。",
        DispatchTaskStepType.PickupFullBox => "28阶段6～7：PLC呼叫保持0、实际无箱，取箱通知和返箱责任读回为1，再清呼叫响应；取箱通知保持到返箱收尾。",
        DispatchTaskStepType.DeliverFullBoxToDumper => "28阶段8：原车端运输任务成功，取箱通知和返箱责任继续保持；到等待点后进入许可握手。",
        DispatchTaskStepType.WaitForDumperReady => "28阶段9～12：本轮设备独占，等待申请/倒料周期/Ready/Done均为旧低基线；实际等待位申请后消费本轮Ready并等Ready回低，设备保留继续。",
        DispatchTaskStepType.EnterDumpingPosition => "28阶段13～14：原车端进入任务成功，前置许可已消费；实际到位后核实Done旧低基线。",
        DispatchTaskStepType.DumpFullBox => "28阶段14～17：倒料周期读回为1，本轮Done从已确认低基线变为1；保持周期及设备占用至实际出区核实。",
        DispatchTaskStepType.ReturnEmptyBox => "28阶段18～21：追踪原返程任务；执行途中核实有效位置及配置的区域包络出区，清倒料周期，等PLC闭合并交接本任务设备/已验证区域；原返程成功且交接完成后才能进入放箱确认。",
        DispatchTaskStepType.PlaceEmptyBox => "28阶段22～23：原返程已含配置放箱动作；放到位通知为1、实际有箱为1，先后顺序不限。",
        DispatchTaskStepType.CompletePlcHandshake => "28阶段24～28：持续核实原返程、原工位有箱及本任务出区交接记录；清返箱责任前要求本轮呼叫0，依次清取箱通知、清放到位、最后清返箱责任。之后保留PLC新呼叫，回读原站通知闭合后结束；不再操作共享倒料位。",
        _ => "查看28阶段持久化检查点。"
    };

    public static string Boundary28(DispatchTaskStepType type) => type switch
    {
        DispatchTaskStepType.ReturnEmptyBox => "真实出区必须启用匹配地图的区域边界、车体/箱体包络和有效定位；缺配置、质量或身份不满足时保持占用，返程接受/成功都不能替代出区证据。仅交接已验证倒料区域，其余道路仍按记录保留。",
        DispatchTaskStepType.CompletePlcHandshake => "前车已完成出区交接后，后车可使用共享倒料位；返箱收尾只核对本任务记录和原工位，不清后车信号或归属。",
        _ => "用户已与电控统一28阶段语义；仍需匹配PLC程序、地图和动作配置并完成实车验证。写调用/回读不等于PLC业务接收或机械完成；不自动迁移旧契约任务。"
    };
}
