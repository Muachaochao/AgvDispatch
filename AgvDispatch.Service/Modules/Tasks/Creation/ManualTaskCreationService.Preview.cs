using System.Globalization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class ManualTaskCreationService
{
    // Preview the configured executor; future PLC phases are not current readiness checks.
    private IReadOnlyList<ManualTaskStepPreview> BuildStepPreview(string stationId, string pickup, ActiveMapRuntimeSnapshot? map, bool preparation = false)
    {
        var waiting = dispatch.GetDumperWaitingSite() ?? "";
        var dumping = dispatch.DumperDumpingSite;
        var dumper = creation.DumperStationId;
        string Action(DispatchTaskStepType type)
        {
            var action = dispatch.GetAction(type);
            if (action is null) return "导航；未配置附加车辆动作";
            var parameters = new List<string> { action.Operation };
            if (action.JackHeight is { } height) parameters.Add("JackHeight=" + height.ToString("G", CultureInfo.InvariantCulture));
            if (action.Recognize is { } recognize) parameters.Add("Recognize=" + recognize);
            if (action.UsePgv is { } pgv) parameters.Add("UsePgv=" + pgv);
            if (action.UseDownPgv is { } down) parameters.Add("UseDownPgv=" + down);
            return "导航 + " + string.Join("，", parameters);
        }
        string Route(string from, string to) => CanRoute(map, from, to) ? "静态路径已连通；执行时重新取得路权" : "路径缺失、站点映射无效或路径节点不足";
        ManualTaskStepPreview[] preview =
        [
            new(1, DispatchTaskStepType.GoToPickup, "前往取货点", "", pickup, stationId,
                Action(DispatchTaskStepType.GoToPickup), "车辆任务完成（首段含配置的附加动作）",
                "派车前由创建协调器写 FullBoxRequestAcknowledged=true", "起点随派车车辆确定；提交及执行时校验首段路径"),
            new(2, DispatchTaskStepType.PickupFullBox, "确认取走满箱", pickup, pickup, stationId,
                "无独立车辆命令；取箱动作由第 1 步配置承载", "BoxPresent=false；FullBoxRemoved=true；EmptyBoxTaskAcknowledged=true",
                "取箱后写 FullBoxRemoved=true、EmptyBoxTaskAcknowledged=true；满足后复位 FullBoxRequestAcknowledged、FullBoxRemoved", "PLC 读取条件均要求 Good 质量"),
            new(3, DispatchTaskStepType.DeliverFullBoxToDumper, "送满箱至倒料等待点", pickup, waiting, dumper,
                Action(DispatchTaskStepType.DeliverFullBoxToDumper), "车辆任务完成", "无独立 PLC 等待；下一步进行倒料就绪握手", Route(pickup, waiting)),
            new(4, DispatchTaskStepType.WaitForDumperReady, "等待倒料机就绪", waiting, waiting, dumper,
                "无独立车辆命令", "Faulted=false；AgvAtWaitingPosition=true；DumperReady=true",
                "写 AgvAtWaitingPosition=true；满足后复位为 false", "PLC 读取条件均要求 Good 质量"),
            new(5, DispatchTaskStepType.EnterDumpingPosition, "进入倒料位", waiting, dumping, dumper,
                Action(DispatchTaskStepType.EnterDumpingPosition), "车辆任务完成", "无独立 PLC 等待；下一步进行倒料完成握手", Route(waiting, dumping)),
            new(6, DispatchTaskStepType.DumpFullBox, "等待倒料完成", dumping, dumping, dumper,
                "无独立车辆命令", "Faulted=false；AgvAtDumpingPosition=true；DumpingCompletedMayLeave=true",
                "写 AgvAtDumpingPosition=true；满足后复位为 false", "PLC 读取条件均要求 Good 质量"),
            new(7, DispatchTaskStepType.ReturnEmptyBox, "返空箱至取货点", dumping, pickup, stationId,
                Action(DispatchTaskStepType.ReturnEmptyBox), "车辆任务完成（含配置的放箱动作）", "无独立 PLC 等待；下一步确认空箱放置", Route(dumping, pickup)),
            new(8, DispatchTaskStepType.PlaceEmptyBox, "确认空箱放置", pickup, pickup, stationId,
                "无独立车辆命令；放箱动作由第 7 步配置承载", "EmptyBoxPlaced=true；BoxPresent=true",
                "写 EmptyBoxPlaced=true；等待箱体在位", "PLC 读取条件均要求 Good 质量"),
            new(9, DispatchTaskStepType.CompletePlcHandshake, "结束 PLC 握手", pickup, pickup, stationId,
                "无独立车辆命令；完成后释放任务路权", "复位写入成功后结束；当前执行器不等待下一轮 PLC 读回",
                "复位 EmptyBoxTaskAcknowledged、EmptyBoxPlaced、FullBoxRequestAcknowledged、FullBoxRemoved 为 false", "按现有执行器行为展示")
        ];
        if(!Uses28Contract(preparation))return preview;
        return preview.Select(step=>step with
        {
            WaitingCondition=TaskStepProgressRules.Requirement28(step.StepType),
            PlcHandshake=step.StepType switch
            {
                DispatchTaskStepType.GoToPickup=>"任务保存后置受理应答1；等待PLC撤满箱呼叫0，满足后才绑定并开始取箱运输",
                DispatchTaskStepType.PickupFullBox=>"取箱后置 FullBoxRemoved、EmptyBoxTaskAcknowledged；持续核实呼叫0再清应答；FullBoxRemoved保持至第25阶段",
                DispatchTaskStepType.WaitForDumperReady=>"取得本轮设备独占，核对申请/倒料周期/Ready/Done旧低后申请；消费Ready，撤申请并等Ready回低，PLC内部设备保留继续",
                DispatchTaskStepType.DumpFullBox=>"核实Done旧低，置AgvAtDumpingPosition；本轮Done=1后仍保持周期，直到实际出区",
                DispatchTaskStepType.ReturnEmptyBox=>"返程执行中核实车体/箱体完全出区；清倒料周期并等待PLC四位低；持久化设备及已验证区域交接，保留返箱责任",
                DispatchTaskStepType.PlaceEmptyBox=>"置EmptyBoxPlaced并核实有箱；不提前清取箱通知或返箱责任",
                DispatchTaskStepType.CompletePlcHandshake=>"持续核对本任务出区交接、原返程及原站有箱；清返箱责任前要求本轮呼叫0，依次清FullBoxRemoved→EmptyBoxPlaced→EmptyBoxTaskAcknowledged（最后）；之后保留新呼叫并回读原站闭合，不操作后车共享位",
                _=>step.PlcHandshake
            },
            ConfigurationNote=step.ConfigurationNote+"；契约 "+HandshakeExecutionOptions.Version+
                (handshakeOptions!.Enabled ? "" : "（已配置；执行未启用，仅作准备校验）")+"；"+TaskStepProgressRules.Boundary28(step.StepType)+
                (dispatch.GetDumperWaitingSites().Count>1 && step.StepType is DispatchTaskStepType.DeliverFullBoxToDumper or DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.EnterDumpingPosition
                    ? "；预览显示默认候车点；执行时从 "+string.Join("、",dispatch.GetDumperWaitingSites())+" 选取空位并持久绑定，同一任务重试不换位" : "")
        }).ToArray();
    }
}
