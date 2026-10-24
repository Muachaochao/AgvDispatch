using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new AgvDispatch.Wpf.App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var client = new NamedPipeTrafficControlClient("AgvDispatch.SettingsSmoke");
        var vm = new ConnectionSettingsViewModel(client);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        var view = new ConnectionSettingsView { DataContext = vm };
        vm.OnNavigatedTo(null!);
        Pump(() => !vm.IsBusy && vm.Configuration is not null);
        Require(vm.CanManage, "connection management permission");
        if (!args.Contains("--after-restart"))
        {
            var recoveryBeforeTask = client.GetExecutionRecoveryAsync(CancellationToken.None);
            var mapBeforeTask = client.GetMapSwitchAsync(CancellationToken.None);
            Pump(() => recoveryBeforeTask.IsCompleted && mapBeforeTask.IsCompleted);
            var recoveryBefore = recoveryBeforeTask.GetAwaiter().GetResult();
            var mapBefore = mapBeforeTask.GetAwaiter().GetResult();
            Require(mapBefore.Mode == MapExecutionMode.Running, "settings fixture is not switching maps");
            Require(!args.Contains("--recovery-closed") || !recoveryBefore.Released,
                "--recovery-closed fixture starts with the recovery barrier closed");
            vm.AddPlcCommand.Execute(); vm.SelectedPlc!.DisplayName = "测试第二台PLC";
            vm.AddAgvCommand.Execute(); vm.SelectedAgv!.DisplayName = "测试第六台AGV";
            vm.SaveCommand.Execute(); Pump(() => !vm.IsBusy);
            Require(vm.Snapshot!.RestartRequired && vm.Plcs.Count == 2 && vm.Agvs.Count == 6, "save multiple profiles through API");
            Require(vm.Snapshot.PlcConnections.Count == 1, "save does not change runtime connections");
            var recoveryAfterTask = client.GetExecutionRecoveryAsync(CancellationToken.None);
            var mapAfterTask = client.GetMapSwitchAsync(CancellationToken.None);
            Pump(() => recoveryAfterTask.IsCompleted && mapAfterTask.IsCompleted);
            var recoveryAfter = recoveryAfterTask.GetAwaiter().GetResult();
            var mapAfter = mapAfterTask.GetAwaiter().GetResult();
            Require(recoveryAfter.Released == recoveryBefore.Released && recoveryAfter.SessionId == recoveryBefore.SessionId,
                "saving configuration preserves recovery release state and session");
            Require(mapAfter.Mode == MapExecutionMode.Running, "saving configuration preserves running map mode");
        }
        else
        {
            Require(!vm.Snapshot!.RestartRequired && vm.Plcs.Count == 2 && vm.Agvs.Count == 6,
                "persisted configuration restored on restart");
            Require(vm.Snapshot.PlcConnections.Count == 2, "two runtime PLC connection supervisors");
            Require(!vm.Configuration!.AgvEnabled && vm.Plcs.All(p => !p.Enabled), "safety switches preserved");
        }
        view.Width = 1280; view.Height = 820;
        view.Background = (Brush)app.FindResource("AppBackgroundBrush");
        Render(view, "plc");
        var tabs = Find<TabControl>(view).First(); tabs.SelectedIndex = 1; Render(view, "agv");
        vm.OnNavigatedFrom(null!);
        Console.WriteLine("PASS: settings XAML, permissions, API save, restart separation, selection and render");
    }
    private static void Render(FrameworkElement view, string tab)
    {
        view.Measure(new Size(1280, 820)); view.Arrange(new Rect(0, 0, 1280, 820)); view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout();
        var image = new RenderTargetBitmap(1280, 820, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(Path.GetTempPath(), $"agv-settings-{tab}-{Guid.NewGuid():N}.png");
        using (var file = File.Create(path)) encoder.Save(file);
        Console.WriteLine(path);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Pump(Func<bool> done)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Require(done(), "timed out: connect isolated service first");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { Console.Error.WriteLine("FAIL: " + message); Environment.Exit(1); }
    }
}
