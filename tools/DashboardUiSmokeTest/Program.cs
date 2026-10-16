using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new AgvDispatch.Wpf.App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var client = DispatchProxy.Create<ITrafficControlClient, DashboardProxy>();
        var proxy = (DashboardProxy)(object)client;
        var vm = new DashboardViewModel(client);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        var view = new DashboardView { DataContext = vm };
        vm.RefreshAsync().GetAwaiter().GetResult();
        Require(vm.Map.Nodes.Count == 6 && vm.Map.Vehicles.Count == 4, "active map and valid positioned robots");
        Require(proxy.MapReads == 1 && proxy.RequestedVersion == "RUNNING-1", "load exact active version, never workspace draft");
        Require(vm.Map.Vehicles.Single(v => v.VehicleId == "AGV-02").Rotation == -90, "radians to inverted screen rotation");
        Require(vm.Map.Vehicles.Single(v => v.VehicleId == "AGV-04").Label.Contains("旧位置"), "stale marker explicitly labelled");
        Require(vm.Vehicles.Count == 5, "unlocalized vehicle still available in parameters");
        Require(vm.OnlineVehicles.Count == 4 && vm.OnlineVehicles.All(v => v.VehicleId != "AGV-04"), "panel only contains online fresh vehicles");
        Require(!vm.IsAgvPanelOpen, "map panel starts collapsed for maximum map area");
        Render(view, app, "collapsed");
        var collapsedWidth = ((FrameworkElement)view.FindName("MapViewport")).ActualWidth;
        Require(((FrameworkElement)view.FindName("OnlineAgvPanel")).Visibility == Visibility.Collapsed, "collapsed panel has no allocated width");
        Require(vm.Map.Project(0, 20).Y < vm.Map.Project(0, 0).Y, "world Y axis reversed for canvas");
        vm.SelectVehicleCommand.Execute("AGV-02");
        Require(vm.IsAgvPanelOpen, "map selection opens AGV panel");
        Require(vm.SelectedVehicle?.VehicleId == "AGV-02" && vm.SelectedVehicle.Heading == "90.0°", "marker selects parameter panel");
        Require(vm.SelectedVehicle!.Speed.Contains("0.50") && vm.SelectedVehicle.Battery == "68%", "velocity and percentage units");
        vm.RefreshAsync().GetAwaiter().GetResult();
        Require(proxy.MapReads == 1 && vm.SelectedVehicle.VehicleId == "AGV-02", "refresh preserves selection and map cache");
        Render(view, app, "live");
        Require(collapsedWidth - ((FrameworkElement)view.FindName("MapViewport")).ActualWidth > 300, "collapse genuinely returns width to map");
        Require(((FrameworkElement)view.FindName("RuntimeMapModule")).ActualHeight > 600, "runtime map is the dominant module");
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)view.FindName("AgvPanelToggle");
        toggle.IsChecked = false;
        Require(!vm.IsAgvPanelOpen, "toggle collapses panel through two-way binding");
        toggle.IsChecked = true;
        Require(vm.IsAgvPanelOpen, "toggle opens panel through two-way binding");
        proxy.Version = "RUNNING-2"; vm.RefreshAsync().GetAwaiter().GetResult();
        Require(proxy.MapReads == 2 && proxy.RequestedVersion == "RUNNING-2", "active version change invalidates map cache");
        proxy.NoMap = true; vm.RefreshAsync().GetAwaiter().GetResult();
        Require(vm.Map.Nodes.Count == 0 && vm.Map.Vehicles.Count == 0 && vm.Vehicles.Count == 5, "no map does not hide vehicle table");
        Render(view, app, "no-map");
        proxy.Fail = true; vm.RefreshAsync().GetAwaiter().GetResult();
        Require(vm.Vehicles.Count == 0 && vm.Map.Vehicles.Count == 0 && vm.OnlineVehicles.Count == 0, "unavailable service clears misleading live values");
        Render(view, app, "offline");
        var scene = new RuntimeMapScene(); scene.SetMap(proxy.Map with { Unit = "mm" });
        scene.UpdateVehicles(proxy.Robots, DateTimeOffset.Now);
        Require(scene.Vehicles.Count == 0 && scene.Message.Contains("坐标转换"), "millimetre CAD coordinates never mixed with metre robot poses");
        Console.WriteLine("PASS: runtime map, exact active version, cache, selection, telemetry, stale/no-position/no-map/offline safeguards and XAML rendering.");
    }
    private static void Render(System.Windows.Controls.Control view, System.Windows.Application app, string name)
    {
        view.Width = 1280; view.Height = 900; view.Background = (Brush)app.FindResource("AppBackgroundBrush");
        view.Measure(new Size(1280, 900)); view.Arrange(new Rect(0, 0, 1280, 900)); view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(), $"agv-dashboard-{name}-{Guid.NewGuid():N}.png");
        using(var file = File.Create(path)) encoder.Save(file); Console.WriteLine(path);
    }
    private static void Require(bool ok, string message)
    { if (!ok) { Console.Error.WriteLine("FAIL: " + message); Environment.Exit(1); } }
}

public class DashboardProxy : DispatchProxy
{
    public bool NoMap, Fail;
    public string Version = "RUNNING-1";
    public string? RequestedVersion;
    public int MapReads;
    public MapDefinition Map { get; } = new("厂区物流示例", "isolated-test", "Robokit", "m",
        [new("CNC-01", 0, 0, MapNodeType.Station, true, []), new("CNC-02", 0, 20, MapNodeType.Station, true, []),
         new("CROSS-01", 20, 0, MapNodeType.Intersection, true, []), new("CROSS-02", 20, 20, MapNodeType.Intersection, true, []),
         new("DUMP-01", 40, 20, MapNodeType.Station, true, []), new("CHARGE-01", 40, 0, MapNodeType.ChargingPoint, true, [])],
        [new("E1", "CNC-01", "CROSS-01", 20, 1, true, true, []), new("E2", "CROSS-01", "CROSS-02", 20, 1, true, true, []),
         new("E3", "CNC-02", "CROSS-02", 20, 1, true, true, []), new("E4", "CROSS-02", "DUMP-01", 20, 1, false, true, []),
         new("E5", "CROSS-01", "CHARGE-01", 20, 1, true, true, [])], []);
    public AgvDiagnostic[] Robots => Enumerable.Range(1, 5).Select(i => new AgvDiagnostic(
        $"AGV-{i:00}", $"物流车 {i:00}", $"192.168.1.{100+i}:19204", i == 4 ? ConnectionState.Interrupted : ConnectionState.Online,
        i == 4 ? SignalQuality.Stale : SignalQuality.Good, 80 - i*6, "CROSS-01", "2", 1, i == 3 ? 1 : 0, false, false,
        i == 5 ? null : i == 2 ? 20 : 5*i, i == 2 ? 10 : i == 3 ? 20 : 0, i == 2 ? Math.PI/2 : 0,
        0.99, i == 4 ? AgvMapPositionState.Stale : AgvMapPositionState.Normal, 0, "E1", "定位正常，最近路段 E1",
        i == 4 ? DateTimeOffset.Now.AddMinutes(-1) : DateTimeOffset.Now, DateTimeOffset.Now, 0, 15, null)
        { VelocityX = 0.3, VelocityY = 0.4, AngularVelocity = 0.1, Charging = false, BatteryVoltage = 51.2, BatteryCurrent = -2, BatteryTemperature = 30 }).ToArray();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Fail) throw new IOException("isolated offline fixture");
        if (method?.Name == "GetMapVersionAsync")
        {
            MapReads++; RequestedVersion = (string)args![1]!;
            return Task.FromResult<MapVersionSnapshot?>(new(Version, "FACTORY", 1, MapVersionStatus.Published, Map,
                new([], 6, 5, 0), "test", DateTimeOffset.Now, DateTimeOffset.Now, DateTimeOffset.Now, "test"));
        }
        if (method?.Name == "GetSystemStatusAsync")
            return Task.FromResult(new SystemStatusSnapshot(DateTimeOffset.Now, new("Test", "1", 1, DateTimeOffset.Now, TimeSpan.FromHours(2)),
                new(true, 1, "test"), new("PLC", "PLC", ConnectionState.Disconnected, 0, DateTimeOffset.Now, null, null, null, 1, 20, 20, 0, 0, 0, 20, 0),
                Robots, new(0, 0, 0, 0, []), new("test", 1, 1, 0, "test", "test", TrafficPermission.View, null, null, null),
                new(false, false, true, false, !NoMap, false, NoMap ? null : "FACTORY", NoMap ? null : Version)));
        throw new NotSupportedException(method?.Name);
    }
}
