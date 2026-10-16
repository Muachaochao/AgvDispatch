using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    [STAThread] static void Main()
    {
        var app = new AgvDispatch.Wpf.App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var client = DispatchProxy.Create<ITrafficControlClient, RecoveryClient>(); var proxy = (RecoveryClient)client;
        var vm = new ExecutionRecoveryViewModel(client);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        var view = new ExecutionRecoveryView { DataContext = vm };
        vm.RefreshAsync().GetAwaiter().GetResult();
        Check(vm.Items.Count == 1 && vm.Audits.Count == 1, "恢复列表与审计加载");
        vm.Selected = vm.Items[0]; vm.Material = "BOX-01"; vm.Reason = "已现场核对车辆原路线载荷和PLC周期";
        vm.VehicleConfirmed = vm.LoadConfirmed = vm.PlcConfirmed = true;
        Check(!vm.ContinueCommand.CanExecute(), "只读账号即使勾选全部条件也不能处置");
        proxy.Permission = TrafficPermission.View | TrafficPermission.RecoverExecution;
        vm.RefreshAsync().GetAwaiter().GetResult();
        Check(vm.ContinueCommand.CanExecute() && vm.CompleteCommand.CanExecute(), "处置权限与现场确认共同控制按钮");
        proxy.Revision++;
        vm.RefreshAsync().GetAwaiter().GetResult();
        Check(!vm.ContinueCommand.CanExecute() && !vm.VehicleConfirmed, "执行节点变化自动清除过期人工勾选");
        proxy.TaskStage = 130;
        vm.RefreshAsync().GetAwaiter().GetResult(); vm.VehicleConfirmed = vm.LoadConfirmed = vm.PlcConfirmed = true;
        Check(vm.CloseCancelledCommand.CanExecute() && !vm.ContinueCommand.CanExecute() && !vm.CompleteCommand.CanExecute(), "取消核对仅开启关闭取消按钮，不允许继续原物理步骤");
        proxy.IdentityBlocked = true; proxy.Revision++;
        vm.RefreshAsync().GetAwaiter().GetResult(); vm.VehicleConfirmed = vm.LoadConfirmed = vm.PlcConfirmed = true;
        Check(!vm.CloseCancelledCommand.CanExecute() && !vm.ContinueCommand.CanExecute() && !vm.CompleteCommand.CanExecute(), "身份冲突全部勾选也禁止恢复处置");
        Check(vm.Selected!.IdentitySummary.Contains("周期号"), "身份冲突原因进入恢复摘要");
        proxy.IdentityBlocked = false; proxy.Revision++;
        vm.RefreshAsync().GetAwaiter().GetResult();
        Check(!vm.VehicleConfirmed && !vm.CloseCancelledCommand.CanExecute(), "身份修订清除旧现场核对");
        vm.VehicleConfirmed = vm.LoadConfirmed = vm.PlcConfirmed = true;
        Check(vm.CloseCancelledCommand.CanExecute(), "未接入字段保留现有人工流程");
        var theme = new ThemeService();
        foreach (var name in new[] { "light", "dark" })
        {
            if (name == "dark") theme.Toggle();
            view = new ExecutionRecoveryView { DataContext = vm };
            view.Width = 1280; view.Height = 800; view.Background = (Brush)app.FindResource("AppBackgroundBrush");
            view.Measure(new Size(1280, 800)); view.Arrange(new Rect(0, 0, 1280, 800)); view.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(((System.Windows.Controls.TextBlock)view.FindName("RecoveryIdentitySummary")).Text.Contains("身份字段"), "身份状态文本绑定显示");
            ((System.Windows.Controls.ScrollViewer)view.FindName("RecoveryAttestationScroll")).ScrollToTop(); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(AppContext.BaseDirectory, "recovery-" + name + ".png"); using var stream = File.Create(file); png.Save(stream); Console.WriteLine(file);
        }
        Check(NavigationCatalog.Items.Any(i => i.Route == "ExecutionRecoveryView"), "侧栏注册重启恢复入口");
        Console.WriteLine("PASS Recovery UI: read-only/permissions, stale evidence reset, navigation and light/dark render.");
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
public class RecoveryClient : DispatchProxy
{
    public TrafficPermission Permission = TrafficPermission.View; public int Revision = 1; public int? TaskStage; public bool IdentityBlocked;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetSessionAsync" => Task.FromResult(new TrafficControlSession("test", "恢复工程师", Permission)),
        "GetExecutionRecoveryAsync" => Task.FromResult(new RecoveryStatus("boot", false, "等待现场核对",
            [new("ticket", "TASK-20261009-001", "STEP-001", "CNC-01", "AGV-01", "PickupFullBox", "等待核对", "PLC动作及物料状态需要现场确认；布尔完成位不能证明本次动作已完成。", "{\"LoadQuality\":\"Unknown\",\"PlcCycleQuality\":\"Unknown\"}", false, null, Revision) { IdentityBlocksRecovery = IdentityBlocked, IdentityState = IdentityBlocked ? "Blocked" : "Unavailable", IdentitySummary = IdentityBlocked ? "身份核验冲突：PLC动作周期号已变化。" : "无法自动核验：设备身份字段尚未接入，保留现场核对。", TaskStage = TaskStage, VehicleSummary = "AGV-01 · 当前站点 CNC-01 · Online/Good · 速度 0/0/0", PlcSummary = "CNC-01/BoxPresent=False（Good）；DUMPER/AgvAtDumpingPosition=False（Good）", ResourceSummary = "原路权无占用；不会自动释放路权。" }],
            [new("audit", "boot", "TASK-20261009-001", "STEP-001", "service", "BootHold", "启动后保留原任务和资源占用，等待本次启动后的新鲜采集。", "snapshot", DateTime.Now)])),
        _ => throw new NotSupportedException(method?.Name)
    };
}
