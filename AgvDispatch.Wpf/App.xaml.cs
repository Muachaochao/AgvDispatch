using System.Text.Json;
using System.Windows;
using System.IO;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using NLog;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Mvvm;

namespace AgvDispatch.Wpf;

public partial class App : PrismApplication
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private NamedPipeTrafficControlClient? _trafficClient;

    protected override Window CreateShell() => Environment.GetCommandLineArgs().Contains("--map-editor", StringComparer.OrdinalIgnoreCase)
        ? new MapEditorWindow() : Container.Resolve<MainWindow>();

    protected override void ConfigureViewModelLocator()
    {
        base.ConfigureViewModelLocator();
        ViewModelLocationProvider.Register<MainWindow, MainWindowViewModel>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<IThemeService, ThemeService>();
        containerRegistry.RegisterSingleton<IUserFeedback, HandyUserFeedback>();
        _trafficClient = new NamedPipeTrafficControlClient(GetTrafficControlPipeName());
        containerRegistry.RegisterInstance<ITrafficControlClient>(_trafficClient);
        containerRegistry.RegisterInstance<IDeviceMonitoringClient>(_trafficClient);
        containerRegistry.RegisterForNavigation<AgvManagementView>();
        containerRegistry.RegisterForNavigation<EquipmentMonitorView>();
        containerRegistry.RegisterForNavigation<DashboardView>();
        containerRegistry.RegisterForNavigation<TrafficResourceMonitorView>("DispatchMonitorView");
        containerRegistry.RegisterForNavigation<MapOverviewView>("MapManagementView");
        containerRegistry.RegisterForNavigation<CommunicationDiagnosticView>("CommunicationDiagnosticView");
        containerRegistry.RegisterForNavigation<AlarmRecordView>("AlarmRecordView");
        containerRegistry.RegisterForNavigation<TaskCenterView>("TaskView");
        containerRegistry.RegisterForNavigation<ExecutionRecoveryView>();
        containerRegistry.RegisterForNavigation<ConnectionSettingsView>("SettingsView");

        foreach (var route in NavigationCatalog.ModuleRoutes.Where(
                     route => route.Route is not (
                         "DispatchMonitorView" or "MapManagementView" or "CommunicationDiagnosticView" or "AlarmRecordView" or "TaskView" or "SettingsView" or "ExecutionRecoveryView" or "AgvManagementView" or "EquipmentMonitorView")))
        {
            containerRegistry.RegisterForNavigation<ModulePageView>(route.Route);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Info("AGV调度上位机启动。Version={0}", typeof(App).Assembly.GetName().Version);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("AGV调度上位机退出。ExitCode={0}", e.ApplicationExitCode);
        LogManager.Shutdown();
        _trafficClient?.Dispose();
        base.OnExit(e);
    }

    private static string GetTrafficControlPipeName()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(settingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (settings.RootElement.TryGetProperty("Service", out var service) &&
                    service.TryGetProperty("PipeName", out var configured) &&
                    !string.IsNullOrWhiteSpace(configured.GetString()))
                {
                    return configured.GetString()!;
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "读取Named Pipe配置失败，使用默认名称。");
        }
        return "AgvDispatch.Service";
    }
}
