using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using Prism.Mvvm;
using Prism.Regions;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var origin = TrafficUserContext.LocalOperation("TEST\\local-user");
        foreach (var permission in Enum.GetValues<TrafficPermission>()) Require(origin.Has(permission), "local mode permits " + permission);
        Require(origin.WindowsIdentity == "TEST\\local-user", "OS origin preserved for audit, no software credentials");
        CheckPipeAsync().GetAwaiter().GetResult();
        var app = new System.Windows.Application();
        foreach (var resource in new[] {
            "pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml",
            "pack://application:,,,/HandyControl;component/Themes/Theme.xaml",
            "pack://application:,,,/AgvDispatch.Wpf;component/Themes/Light.xaml",
            "pack://application:,,,/AgvDispatch.Wpf;component/Styles/Typography.xaml",
            "pack://application:,,,/AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml",
            "pack://application:,,,/AgvDispatch.Wpf;component/Styles/Controls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(resource) });
        var theme = new ThemeService(); var vm = new MainWindowViewModel(new RegionManager(), theme);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        Require(typeof(MainWindow).Assembly.GetType("AgvDispatch.Wpf.LoginWindow") is null, "login window removed from assembly");
        Require(typeof(MainWindow).Assembly.GetType("AgvDispatch.Wpf.UserManagementWindow") is null, "user management removed from assembly");
        var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "AgvDispatch.Wpf", "Shell", "MainWindow.xaml"));
        var xaml = source.Replace("x:Class=\"AgvDispatch.Wpf.MainWindow\"", "")
            .Replace("clr-namespace:AgvDispatch.Wpf.Services\"", "clr-namespace:AgvDispatch.Wpf.Services;assembly=AgvDispatch.Wpf\"")
            .Replace("prism:ViewModelLocator.AutoWireViewModel=\"True\"", "")
            .Replace("prism:RegionManager.RegionName=\"ContentRegion\"", "")
            .Replace("\"Assets/", "\"pack://application:,,,/AgvDispatch.Wpf;component/Assets/");
        xaml = System.Text.RegularExpressions.Regex.Replace(xaml, " (Loaded|Click)=\"[^\"]*\"", "");
        var shell = (Window)System.Windows.Markup.XamlReader.Parse(xaml); shell.DataContext = vm;
        var content = (FrameworkElement)shell.Content;
        Render(content, "no-login-header", 1440, 900);
        Require(((Grid)content).RowDefinitions[0].ActualHeight == 52, "compact header");
        Require(vm.CurrentPage == "总览", "default page is overview");
        Require(!source.Contains("切换登录") && !source.Contains("用户管理") && !source.Contains("CurrentRole"), "settings contains no login, users or role UI");
        Require(((TextBlock)shell.FindName("HeaderClock")).Text == vm.HeaderTime && ((TextBlock)shell.FindName("HeaderClockSeconds")).Text == vm.HeaderSeconds && ((TextBlock)shell.FindName("HeaderClockDate")).Text == vm.HeaderDate, "date, weekday and segmented seconds directly visible");
        Require(((Border)shell.FindName("HeaderBrandIcon")).ActualHeight == 36 && ((Border)shell.FindName("HeaderClockPanel")).ActualHeight == 36, "stronger icon and instrument clock fit unchanged header");
        Render(content, "compact-header-1180", 1180, 720);
        Require(((TextBlock)shell.FindName("HeaderClockDate")).ActualWidth > 65 && ((TextBlock)shell.FindName("HeaderClockWeekday")).Text == vm.HeaderWeekday, "date and weekday remain visible at minimum window width");
        Require(MainWindowViewModel.FormatTimestamp(DateTimeOffset.Parse("2026-10-09T03:20:20Z")) == "2026.10.9 星期五 11:20:20", "Shanghai date and weekday");
        var navigation = Find<Button>(content).First(b => b.DataContext is NavigationItem);
        navigation.ApplyTemplate();
        var surface = (Border)navigation.Template.FindName("Root", navigation);
        var transforms = (TransformGroup)surface.RenderTransform;
        Require(VisualStateManager.GoToState(navigation, "MouseOver", true), "navigation hover state exists"); Pump(220);
        Require(Math.Abs(((TranslateTransform)transforms.Children[1]).X - 3) < 0.01 && Math.Abs(((ScaleTransform)transforms.Children[0]).ScaleX - 1.012) < 0.001, "hover advances and scales without layout change");
        Require(VisualStateManager.GoToState(navigation, "Pressed", true), "navigation pressed state exists"); Pump(200);
        Require(Math.Abs(((ScaleTransform)transforms.Children[0]).ScaleX - 0.985) < 0.001, "press scales down");
        VisualStateManager.GoToState(navigation, "Normal", true); Pump(200);
        Require(Math.Abs(((TranslateTransform)transforms.Children[1]).X) < 0.01 && Math.Abs(((ScaleTransform)transforms.Children[0]).ScaleX - 1) < 0.001, "hover/press restore normally");
        ((NavigationItem)navigation.DataContext).IsSelected = true; Pump(220);
        var selection = (Border)navigation.Template.FindName("SelectionBar", navigation);
        Require(selection.Opacity > 0.99 && ((ScaleTransform)selection.RenderTransform).ScaleY > 0.99, "selection marker animates open");
        Render(content, "unified-clock-selected-navigation", 1180, 720);
        ((NavigationItem)navigation.DataContext).IsSelected = false; Pump(200);
        Require(selection.Opacity < 0.01, "selection marker retracts");
        var sidebar = (Border)shell.FindName("SidebarPanel");
        var toggle = (ToggleButton)shell.FindName("SidebarToggle");
        Require(Math.Abs(sidebar.ActualWidth - SidebarMotion.ExpandedWidth) < 1, "sidebar expanded initially");
        toggle.IsChecked = false; Pump(320);
        Render(content, "collapsed-icon-sidebar", 1180, 720);
        Require(!vm.IsSidebarExpanded && Math.Abs(sidebar.ActualWidth - SidebarMotion.CollapsedWidth) < 1, "toggle collapses sidebar with width animation");
        Require(Find<Button>(sidebar).Count(b => b.DataContext is NavigationItem) == vm.NavigationItems.Count, "all navigation buttons retained in compact mode");
        Require(Find<Button>(sidebar).Where(b => b.DataContext is NavigationItem).All(b => b.ToolTip is string title && title.Length > 0), "compact buttons retain names via tooltips");
        toggle.IsChecked = true; Pump(320); content.Measure(new Size(1180,720)); content.Arrange(new Rect(0,0,1180,720)); content.UpdateLayout();
        Require(vm.IsSidebarExpanded && Math.Abs(sidebar.ActualWidth - SidebarMotion.ExpandedWidth) < 1, "toggle restores expanded width");
        toggle.IsChecked = false; Pump(60); toggle.IsChecked = true; Pump(320); content.UpdateLayout();
        Require(Math.Abs(sidebar.ActualWidth - SidebarMotion.ExpandedWidth) < 1, "rapid toggle replaces in-flight animation");
        var popup = (Popup)shell.FindName("SettingsPopup"); ((FrameworkElement)popup.Child).DataContext = vm;
        Render((FrameworkElement)popup.Child, "appearance-settings", 300, 130);
        Require(Find<Button>(popup.Child).Count() == 1, "appearance settings only");
        vm.ToggleThemeCommand.Execute(); Require(theme.IsDark, "theme switching works"); vm.ToggleThemeCommand.Execute();
        var code = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "AgvDispatch.Wpf", "Shell", "MainWindow.xaml.cs"));
        Require(!code.Contains("ShowDialog") && !code.Contains("Opacity = 0") && code.Contains("LoadedCommand.Execute"), "startup directly loads overview without login gate");
        Console.WriteLine("PASS: no-login startup, no account/role UI, appearance settings, timestamp, local operation context and credential-free Pipe request.");
    }
    private static async Task CheckPipeAsync()
    {
        var name = "AgvDispatch.NoLoginTest." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var response = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var header = new byte[4]; await server.ReadExactlyAsync(header, timeout.Token);
            var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header)]; await server.ReadExactlyAsync(body, timeout.Token);
            using var json = System.Text.Json.JsonDocument.Parse(body);
            Require(json.RootElement.GetProperty("operation").GetString() == "GetSession" && !json.RootElement.TryGetProperty("sessionToken", out _), "request needs no token/login");
            var user = TrafficUserContext.LocalOperation(server.GetImpersonationUserName());
            var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var result = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new TrafficControlPipeResponse(true, null,
                System.Text.Json.JsonSerializer.SerializeToElement(new TrafficControlSession(user.WindowsIdentity, user.DisplayName, user.Permissions), options)), options);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, result.Length);
            await server.WriteAsync(header, timeout.Token); await server.WriteAsync(result, timeout.Token); await server.FlushAsync(timeout.Token);
        });
        using var client = new NamedPipeTrafficControlClient(name);
        var context = await client.GetSessionAsync(timeout.Token);
        Require(context.Permissions.HasFlag(TrafficPermission.Administer), "business context available without authentication");
        await response;
    }
    private static void Render(FrameworkElement element, string name, int width, int height)
    {
        if (element is Grid grid) grid.Background = (Brush)System.Windows.Application.Current.FindResource("AppBackgroundBrush");
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(), $"agv-{name}-{Guid.NewGuid():N}.png"); using var file = File.Create(path); encoder.Save(file); Console.WriteLine(path);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T typed) yield return typed; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Require(bool pass, string message) { if (!pass) throw new Exception("FAIL " + message); }
}
