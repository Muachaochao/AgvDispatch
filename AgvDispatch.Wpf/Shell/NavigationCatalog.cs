namespace AgvDispatch.Wpf.ViewModels;

public static class NavigationCatalog
{
    public const string ContentRegion = "ContentRegion";
    private const string IconBasePath = "/AgvDispatch.Wpf;component/Assets/Icons/Png/";

    public static readonly NavigationItem Dashboard = new(
        "DashboardView", "总览", IconBasePath + "overview.png", "系统运行状态、任务、设备、报警和关键指标总览");

    public static IReadOnlyList<NavigationItem> Items { get; } =
    [
        Dashboard,
        new("DispatchMonitorView", "调度监控", IconBasePath + "dispatch-monitor.png", "AGV位置、运行路线、交通管制和占用资源实时监控"),
        new("MapManagementView", "地图管理", IconBasePath + "equipment-monitor.png", "查看调度地图配置与规划拓扑，进入独立编辑器更新发布"),
        new("TaskView", "任务中心", IconBasePath + "task-center.png", "回收任务的创建、排队、执行、异常恢复和历史查询"),
        new("ExecutionRecoveryView", "现场恢复", IconBasePath + "task-center.png", "启动屏障、现场状态核对、原任务恢复及处置审计"),
        new("AgvManagementView", "AGV管理", IconBasePath + "agv-management.png", "车辆位置、电量、任务绑定、厂家报警与数据质量的只读监控"),
        new("EquipmentMonitorView", "设备监控", IconBasePath + "equipment-monitor.png", "多PLC连接、排屑工位业务状态与倒料机原始握手信号"),
        new("CommunicationDiagnosticView", "通信诊断", IconBasePath + "communication-diagnostic.png", "PLC、AGV接口、心跳、点位报文及数据库连接诊断"),
        new("AlarmRecordView", "报警记录", IconBasePath + "alarm-record.png", "当前与历史报警的查询、确认、恢复和追溯"),
        new("SettingsView", "系统设置", IconBasePath + "system-settings.png", "多PLC与多AGV连接配置、采集开关及通信测试")
    ];

    public static IReadOnlyList<NavigationItem> ModuleRoutes => Items.Skip(1).ToArray();
}
