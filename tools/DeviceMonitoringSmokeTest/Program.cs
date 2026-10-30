using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    private static int _checks;
    [STAThread] private static void Main()
    {
        var app = new System.Windows.Application();
        foreach (var uri in new[] { "HandyControl;component/Themes/SkinDefault.xaml", "HandyControl;component/Themes/Theme.xaml",
            "AgvDispatch.Wpf;component/Themes/Light.xaml", "AgvDispatch.Wpf;component/Themes/HandyPalette.xaml", "AgvDispatch.Wpf;component/Styles/Typography.xaml",
            "AgvDispatch.Wpf;component/Styles/IconGlyphs.xaml", "AgvDispatch.Wpf;component/Styles/Controls.xaml",
            "AgvDispatch.Wpf;component/Styles/HandyControls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + uri) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var options = new AgvOptions { Robots = [new() { VehicleId = "AGV-01", DisplayName = "运输一号", IpAddress = "192.0.2.1" },
            new() { VehicleId = "AGV-02", DisplayName = "运输二号", IpAddress = "192.0.2.2" }] };
        var cache = new AgvStateCache(options);
        // Gateways are constructed disabled with empty catalogs; no sockets or PLC authorization involved.
        using var fleet = new PlcFleet([new() { DeviceId = "PLC-A", DisplayName = "区域A", SignalCsvPath = "" },
            new() { DeviceId = "PLC-B", DisplayName = "区域B", SignalCsvPath = "" }]);
        var catalog = PlcSignalCatalog.FromSignals([new("DB1.DBX0.0", "Faulted", "故障", "TEST", "Dumper", 1,
            "Bool", "PlcToHost", "Read", "Confirmed", true, "", "", "")]);
        var signals = new PlcSignalCache(catalog);
        var service = new DeviceMonitoringService(cache, options, fleet, signals, new Stations(), new Tasks());
        var initial = service.GetSnapshotAsync(default).GetAwaiter().GetResult();
        Check(initial.Vehicles.Count == 2 && initial.Equipment.Count == 2, "configured roster and multiple PLCs");
        Check(initial.Vehicles.All(v => v.Status == "通信未启用"), "disabled not online");
        Check(initial.Vehicles[0].Fields.First(f => f.Name == "电量（%）").Value == "—", "unknown battery not zero");
        options.Enabled = true;
        cache.SetCommunicationEnabled("AGV-01", true);
        cache.SetCommunicationEnabled("AGV-02", true);
        var unknown = service.GetSnapshotAsync(default).GetAwaiter().GetResult();
        // Cache initially disabled stays disabled until a first valid telemetry observation.
        Check(unknown.Vehicles[0].Quality != "正常", "no fabricated first reading");
        var now = DateTimeOffset.UtcNow;
        cache.ApplyTelemetry("AGV-01", new(1.2, 3.4, 0.5, 0.99, "LM-01", null, 80, false, true,
            "IDLE", null, null, null, [new("STEP-1", 2, 1)], [new("E001", AgvAlarmSeverity.Error, "测试报警")], now), TimeSpan.FromMilliseconds(25));
        var live = service.GetSnapshotAsync(default).GetAwaiter().GetResult();
        Check(live.Vehicles[0].Status == "急停" && live.Vehicles[0].Quality == "正常",
            $"fault state separate from signal quality (status={live.Vehicles[0].Status}, quality={live.Vehicles[0].Quality}, " +
            $"collection={options.Enabled}, vehicle={options.Robots[0].Enabled})");
        Check(live.Vehicles[0].Fields.Any(f => f.Name.Contains("STEP-1")), "manufacturer task details");
        Check(live.Vehicles[0].Fields.Any(f => f.Name.Contains("E001")), "alarm details");
        Check(live.Vehicles[0].Fields.Any(f => f.Value.Contains("BUSINESS-1")), "bound business task");
        cache.ApplyTelemetry("AGV-01", new(1, 2, null, null, null, null, 80, false, false, "IDLE",
            null, null, null, [], [], now.AddMinutes(-1)), TimeSpan.Zero);
        var stale = service.GetSnapshotAsync(default).GetAwaiter().GetResult();
        Check(stale.Vehicles[0].Quality == "数据过期" && stale.Vehicles[0].Status == "数据过期", "stale telemetry cannot look healthy");
        var json = JsonSerializer.Serialize(live);
        Check(JsonSerializer.Deserialize<DeviceMonitoringSnapshot>(json)!.Vehicles.Count == 2, "IPC contract roundtrip");
        var plcOption = new PlcOptions { DeviceId = "PLC-REAL", SignalCsvPath = Path.GetFullPath("AgvDispatch.Service/config/plc-signals.csv") };
        using var stationFleet = new PlcFleet([plcOption]);
        var stationSignals = new PlcSignalCache(stationFleet.Catalog);
        var stationService = new DeviceMonitoringService(cache, options, stationFleet, stationSignals, new Stations(), new Tasks());
        var disabledStations = stationService.GetSnapshotAsync(default).GetAwaiter().GetResult();
        Check(disabledStations.Equipment.Count > 300 && disabledStations.Equipment.First(d => d.Id == "DUMPER-001").Status == "通信未启用", "CSV equipment roster / disabled stations");
        plcOption.Enabled = true; // Only cache observation tests; never invoke any gateway methods.
        var dumperPoints = stationFleet.Catalog.Signals.Where(s => s.StationId == "DUMPER-001").ToArray();
        stationSignals.ApplyReadings(dumperPoints.Select(s => new PlcSignalReading(s.StationId, s.VariableName, s.VariableName == "Faulted", DateTimeOffset.UtcNow)).ToArray());
        var fault = stationService.GetSnapshotAsync(default).GetAwaiter().GetResult();
        Check(fault.Equipment.First(d => d.Id == "DUMPER-001").Status == "故障", "PLC dumper fault");
        stationSignals.ApplyReadings(dumperPoints.Select(s => new PlcSignalReading(s.StationId, s.VariableName, s.VariableName == "DumperReady", DateTimeOffset.UtcNow)).ToArray());
        var ready = stationService.GetSnapshotAsync(default).GetAwaiter().GetResult();
        Check(ready.Equipment.First(d => d.Id == "DUMPER-001").Status == "准备就绪", "PLC dumper ready");
        Check(ready.Equipment.First(d => d.Id == "CNC-002").Quality != "正常", "disabled inferred points not live");
        stationSignals.ApplyReadings(dumperPoints.Select(s => new PlcSignalReading(s.StationId, s.VariableName, true, DateTimeOffset.UtcNow.AddMinutes(-1))).ToArray());
        var expired = stationService.GetSnapshotAsync(default).GetAwaiter().GetResult().Equipment.First(d => d.Id == "DUMPER-001");
        Check(expired.Status == "信号未就绪" && expired.Fields.Any(f => f.Quality == "数据过期"), "stale PLC blocks live business display");
        var client = new Client(live);
        var protocol = DispatchProxy.Create<ITrafficControlClient, InspectionProxy>();
        var agv = new AgvManagementViewModel(client, protocol);
        agv.OnNavigatedTo(null!);
        Check(agv.Devices.Count == 2, "vehicle page populated");
        agv.Search = "AGV-01";
        Check(agv.Devices.Count == 1, "search");
        agv.Search = ""; agv.AttentionOnly = true;
        Check(agv.Devices.Any(d => d.Status == "急停"), "healthy quality but emergency included in attention filter");
        agv.AttentionOnly = false; agv.SelectedDevice = agv.Devices[1];
        agv.RefreshAsync().GetAwaiter().GetResult();
        Check(agv.SelectedDevice?.Id == "AGV-02", "selection preserved");
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => agv);
        agv.InspectRobokitAsync().GetAwaiter().GetResult();
        Check(agv.RobotStations.Count == 1 && agv.RobotMappings.Count == 1 && agv.InspectionSummary.Contains("3.4.8"), "Robokit read-only result displayed");
        var agvView = new AgvManagementView { DataContext = agv };
        ((System.Windows.Controls.Expander)agvView.FindName("RobokitInspectionPanel")).IsExpanded = true;
        Render(agvView, app, "agv-management");
        agv.SelectedDevice = agv.Devices[0];
        Check(agv.RobotStations.Count == 0 && agv.RobotMappings.Count == 0, "changing vehicle clears inspection evidence");
        agv.SelectedDevice = agv.Devices[1];
        agv.InspectRobokitAsync().GetAwaiter().GetResult();
        agv.SourceNodeId = "N2";
        Check(agv.RobotStations.Count == 0, "changing route clears inspection evidence");
        ((InspectionProxy)(object)protocol).Unavailable = true;
        agv.InspectRobokitAsync().GetAwaiter().GetResult();
        Check(agv.RobotStations.Count == 0 && agv.RobotMappings.Count == 0, "unavailable inspection clears previous evidence");
        client.Fail = true;
        agv.RefreshAsync().GetAwaiter().GetResult();
        Check(agv.Devices.Count == 0 && agv.SelectedDevice is null && agv.Message.Contains("连接失败"), "service failure clears stale healthy display");
        agv.OnNavigatedFrom(null!);
        var calls = client.Calls; agv.RefreshAsync().GetAwaiter().GetResult();
        Check(client.Calls == calls, "no requests after navigation away");
        client.Fail = false;
        var eq = new EquipmentMonitorViewModel(client); eq.OnNavigatedTo(null!);
        Check(eq.Devices.Count == 2 && eq.Devices.All(d => d.Category == "PLC"), "equipment page multi PLC");
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => eq);
        Render(new EquipmentMonitorView { DataContext = eq }, app, "equipment-monitor");
        new AgvDispatch.Wpf.Services.ThemeService().Toggle();
        Render(new EquipmentMonitorView { DataContext = eq }, app, "equipment-monitor-dark");
        eq.OnNavigatedFrom(null!);
        Console.WriteLine($"PASS: {_checks} monitoring checks; no device network or commands.");
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        _checks++; Console.WriteLine("PASS " + name);
    }
    private static void Render(FrameworkElement view, System.Windows.Application app, string name)
    {
        view.Width = 1600; view.Height = 850; ((System.Windows.Controls.Control)view).Background = (Brush)app.FindResource("AppBackgroundBrush");
        view.Measure(new(1600,850)); view.Arrange(new(0,0,1600,850)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1600,850,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(), name + "-smoke.png");
        using (var stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine(path); Check(view.ActualWidth == 1600, name + " XAML render");
    }
    public class InspectionProxy : DispatchProxy
    {
        public bool Unavailable;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if(method?.Name != "GetRobokitInspectionAsync") throw new NotSupportedException();
            var query=(RobokitInspectionQuery)args![0]!;
            return Task.FromResult(new RobokitInspectionSnapshot(query.VehicleId,DateTimeOffset.Now,!Unavailable,!Unavailable,"只读采集完成",
                "3.4.8","1","demo","abc",1,"MAP","V1",true,"sim-owner",
                [new("LM1","ActionPoint",1,2,1.57,"模拟取货站")],
                [new("N1","LM1",true,"车端存在；未核定地图关联")],[],["只读，不作调度放行"]));
        }
    }
    private sealed class Client(DeviceMonitoringSnapshot snapshot) : IDeviceMonitoringClient
    {
        public bool Fail; public int Calls;
        public Task<DeviceMonitoringSnapshot> GetDeviceMonitoringAsync(CancellationToken ct)
        { Calls++; return Fail ? Task.FromException<DeviceMonitoringSnapshot>(new IOException("测试断线")) : Task.FromResult(snapshot); }
    }
    private sealed class Stations : IStationStateService
    {
        public event Action<StationStateChanged>? StationStateChanged { add {} remove {} }
        public long Version => 0;
        public IReadOnlyList<StationStateSnapshot> GetAll() => [];
        public bool TryGet(string id, out StationStateSnapshot snapshot) { snapshot = null!; return false; }
    }
    private sealed class Tasks : IActiveTaskLookup
    {
        public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ActiveDispatchTask>>([new("BUSINESS-1","CNC-001","AGV-01",null,
                DispatchTaskStage.WaitingForVehicle,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)]);
    }
}
