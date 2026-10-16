using System.Globalization;
using System.Windows.Data;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Wpf.Converters;

public sealed class TaskLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DispatchTaskStage s => s switch
        {
            DispatchTaskStage.Created => "等待PLC应答", DispatchTaskStage.WaitingForVehicle => "待派车",
            DispatchTaskStage.SubmittingToAgv => "下发中", DispatchTaskStage.GoingToPickup => "前往取箱",
            DispatchTaskStage.PickingUp => "取满箱", DispatchTaskStage.GoingToDumper => "前往倒料机",
            DispatchTaskStage.WaitingForDumper => "等待倒料机", DispatchTaskStage.Dumping => "倒料中",
            DispatchTaskStage.ReturningEmptyBox => "空箱返回", DispatchTaskStage.WaitingForPlcConfirmation => "等待PLC收尾",
            DispatchTaskStage.CancellationPending => "取消待确认", DispatchTaskStage.CancellationReview => "取消待核对", DispatchTaskStage.Completed => "已完成", DispatchTaskStage.Faulted => "失败", DispatchTaskStage.Cancelled => "已取消", _ => s.ToString()
        },
        DispatchTaskStepType s => s switch
        {
            DispatchTaskStepType.GoToPickup => "前往CNC", DispatchTaskStepType.PickupFullBox => "取满箱握手",
            DispatchTaskStepType.DeliverFullBoxToDumper => "送往倒料等待位", DispatchTaskStepType.WaitForDumperReady => "等待倒料机Ready",
            DispatchTaskStepType.EnterDumpingPosition => "进入倒料位", DispatchTaskStepType.DumpFullBox => "倒料握手",
            DispatchTaskStepType.ReturnEmptyBox => "空箱返回", DispatchTaskStepType.PlaceEmptyBox => "空箱放置握手",
            DispatchTaskStepType.CompletePlcHandshake => "PLC收尾", _ => s.ToString()
        },
        DispatchTaskStepStatus s => s switch
        {
            DispatchTaskStepStatus.Pending => "未激活", DispatchTaskStepStatus.Ready => "待处理/等待握手",
            DispatchTaskStepStatus.CommandPending => "命令待发送", DispatchTaskStepStatus.Sending => "发送中",
            DispatchTaskStepStatus.Executing => "执行中", DispatchTaskStepStatus.Completed => "已完成",
            DispatchTaskStepStatus.RetryScheduled => "等待重试", DispatchTaskStepStatus.Failed => "失败",
            DispatchTaskStepStatus.Cancelled => "已取消", DispatchTaskStepStatus.ManualIntervention => "人工处理", _ => s.ToString()
        },
        AgvCommandStatus s => s switch
        {
            AgvCommandStatus.Pending => "待发送", AgvCommandStatus.Sending => "发送中", AgvCommandStatus.RetryScheduled => "等待重试",
            AgvCommandStatus.Sent => "已发送", AgvCommandStatus.Failed => "失败", AgvCommandStatus.Cancelled => "已取消", _ => s.ToString()
        },
        SignalQuality q => q switch { SignalQuality.Good => "有效", SignalQuality.Stale => "过期", SignalQuality.Bad => "无效",
            SignalQuality.Disabled => "禁用", _ => "未知" },
        bool b => b ? "是" : "否",
        _ => value?.ToString() ?? "—"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
