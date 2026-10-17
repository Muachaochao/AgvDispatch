using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    private static int _renders;
    private static readonly string Output = Path.GetFullPath("docs/screenshots/wpf-full-20261017");
    [STAThread] private static void Main()
    {
        var app = new AgvDispatch.Wpf.App(); app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var traffic = DispatchProxy.Create<ITrafficControlClient, Disconnected>();
        var monitoring = DispatchProxy.Create<IDeviceMonitoringClient, Disconnected>();
        var theme = new ThemeService();
        foreach (var mode in new[] { "light", "dark" })
        {
            if (mode == "dark") theme.Toggle();
            var pages = new (string Name, object Model, Func<UserControl> Create)[]
            {
                ("overview", new DashboardViewModel(traffic), () => new DashboardView()),
                ("tasks", new TaskCenterViewModel(traffic), () => new TaskCenterView()),
                ("agv", new AgvManagementViewModel(monitoring, traffic), () => new AgvManagementView()),
                ("equipment", new EquipmentMonitorViewModel(monitoring), () => new EquipmentMonitorView()),
                ("traffic", new TrafficResourceMonitorViewModel(traffic), () => new TrafficResourceMonitorView()),
                ("recovery", new ExecutionRecoveryViewModel(traffic), () => new ExecutionRecoveryView()),
                ("diagnostics", new CommunicationDiagnosticViewModel(traffic), () => new CommunicationDiagnosticView()),
                ("alarms", new AlarmRecordViewModel(traffic), () => new AlarmRecordView()),
                ("settings", new ConnectionSettingsViewModel(traffic), () => new ConnectionSettingsView()),
                ("maps", new MapOverviewViewModel(traffic), () => new MapOverviewView()),
                ("map-editor", new MapManagementViewModel(traffic), () => new MapManagementView()),
                ("point-cloud", new object(), () => new PointCloudView())
            };
            foreach (var page in pages)
            {
                ViewModelLocationProvider.SetDefaultViewModelFactory(_ => page.Model);
                var view = page.Create(); view.DataContext = page.Model;
                // Exercise connection-failure rendering using an in-process proxy, never a service pipe.
                if (page.Name != "map-editor" && page.Model.GetType().GetProperty("RefreshCommand")?.GetValue(page.Model) is ICommand command && command.CanExecute(null))
                    command.Execute(null);
                Render(view, $"{page.Name}-{mode}", page.Name == "map-editor" ? 1680 : 1280, 850);
                Render(view, $"{page.Name}-{mode}-compact", page.Name == "map-editor" ? 1280 : 922, page.Name == "map-editor" ? 760 : 668);
                if (page.Name is "alarms" or "diagnostics" or "traffic" or "recovery")
                {
                    var title = Find<TextBlock>(view).First(t => t.Style == (Style)app.FindResource("PageTitleTextStyle"));
                    var action = Find<Button>(view).First(b => b.Content is "立即刷新" or "重新采集状态");
                    var titleBounds = title.TransformToAncestor(view).TransformBounds(new Rect(title.RenderSize));
                    var actionBounds = action.TransformToAncestor(view).TransformBounds(new Rect(action.RenderSize));
                    Require(actionBounds.Top >= titleBounds.Bottom, page.Name + " toolbar does not cover its title");
                }
                // Every tab's content must instantiate, including settings forms and diagnostic tables.
                var tabs = (page.Name == "map-editor" ? view.FindName("DetailsTabs") as TabControl : null) ?? Find<TabControl>(view).FirstOrDefault();
                if (tabs != null)
                {
                    if (page.Model is MapManagementViewModel editor) { editor.ShowDetails = true; view.UpdateLayout(); }
                    for (var i = 1; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        Render(view, $"{page.Name}-{mode}-tab-{i}", page.Name == "map-editor" ? 1280 : 922, page.Name == "map-editor" ? 760 : 668);
                    }
                }
            }
            var mapModel = new MapManagementViewModel(traffic);
            ViewModelLocationProvider.SetDefaultViewModelFactory(_ => mapModel);
            var dialog = new NewMapWindow(mapModel);
            var dialogContent = (FrameworkElement)dialog.Content; dialog.Content = null;
            var dialogFrame = new Border { Background = (Brush)app.FindResource("AppBackgroundBrush"), Child = dialogContent };
            dialogFrame.SetValue(TextBlock.ForegroundProperty, app.FindResource("TextPrimaryBrush"));
            dialogFrame.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Microsoft YaHei UI"));
            dialogFrame.SetValue(TextBlock.FontSizeProperty, 13.0);
            Render(dialogFrame, $"new-map-{mode}", 520, 620);
            Require(dialog.ResizeMode == ResizeMode.CanResize, "new map dialog can expand with its content");
        }
        Require(_renders >= 50, "all pages and tabs rendered in both themes");
        Console.WriteLine($"PASS: {_renders} WPF page/tab renders in light/dark and compact layouts; disconnected in-process clients, no device network or writes. {Output}");
    }
    private static void Render(FrameworkElement view, string name, int width, int height)
    {
        view.Width = width; view.Height = height;
        if (view is Control control && control.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue)
            control.SetResourceReference(Control.BackgroundProperty, "AppBackgroundBrush");
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout();
        Require(view.ActualWidth == width && view.ActualHeight == height, name + " target dimensions");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Output); using var stream = File.Create(Path.Combine(Output, name + ".png")); png.Save(stream);
        _renders++;
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
public class Disconnected : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new IOException("离线界面验证：后台未连接");
}
