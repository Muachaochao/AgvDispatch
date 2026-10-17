using System.Collections.ObjectModel;
using NLog;
using System.Windows.Threading;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;
using System.Globalization;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class MainWindowViewModel : BindableBase
{
    public string ClientBuild => typeof(MainWindowViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
    public string WindowTitle => "铝屑回收智能调度平台 · " + ClientBuild;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IRegionManager _regionManager;
    private readonly IThemeService _themeService;
    private readonly DispatcherTimer _clock;
    private string _currentTime = string.Empty;
    private string _currentTimestamp = string.Empty;
    private string _currentPage = "总览";
    private bool _isSidebarExpanded = true;
    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set { if (SetProperty(ref _isSidebarExpanded, value)) { RaisePropertyChanged(nameof(SidebarToggleGlyph)); RaisePropertyChanged(nameof(SidebarToggleHint)); } }
    }
    public string SidebarToggleGlyph => IsSidebarExpanded ? "\uE76B" : "\uE76C";
    public string SidebarToggleHint => IsSidebarExpanded ? "收起导航" : "展开导航";

    public MainWindowViewModel(IRegionManager regionManager, IThemeService themeService)
    {
        _regionManager = regionManager;
        _themeService = themeService;
        NavigationItems = new ObservableCollection<NavigationItem>(NavigationCatalog.Items);
        NavigateCommand = new DelegateCommand<NavigationItem>(Navigate);
        LoadedCommand = new DelegateCommand(() => Navigate(NavigationCatalog.Dashboard));
        ToggleThemeCommand = new DelegateCommand(ToggleTheme);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }
    public DelegateCommand<NavigationItem> NavigateCommand { get; }
    public DelegateCommand LoadedCommand { get; }
    public DelegateCommand ToggleThemeCommand { get; }

    public string CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
    }

    public string CurrentTimestamp { get => _currentTimestamp; private set => SetProperty(ref _currentTimestamp, value); }
    public string HeaderDate => _currentTimestamp.Split(' ')[0];
    public string HeaderWeekday => _currentTimestamp.Split(' ') is { Length: >= 2 } parts ? parts[1] : "";
    public string HeaderTime => _currentTimestamp.Length >= 8 ? _currentTimestamp[^8..^3] : "--:--";
    public string HeaderSeconds => _currentTimestamp.Length >= 2 ? _currentTimestamp[^2..] : "--";
    public static string FormatTimestamp(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"))
            .ToString("yyyy.M.d dddd HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"));

    public string CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string VersionText => $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";
    public string ThemeLabel => _themeService.IsDark ? "浅色外观" : "深色外观";
    public string ThemeGlyph => _themeService.IsDark ? "\uE706" : "\uE708";
    public string CurrentDate => DateTime.Now.ToString("yyyy.MM.dd  dddd");
    private void UpdateClock()
    {
        CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        CurrentTimestamp = FormatTimestamp(DateTimeOffset.UtcNow);
        RaisePropertyChanged(nameof(HeaderDate)); RaisePropertyChanged(nameof(HeaderWeekday)); RaisePropertyChanged(nameof(HeaderTime)); RaisePropertyChanged(nameof(HeaderSeconds));
        RaisePropertyChanged(nameof(CurrentDate));
    }

    private void Navigate(NavigationItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var navigationItem in NavigationItems)
            navigationItem.IsSelected = ReferenceEquals(navigationItem, item) || navigationItem.Route == item.Route;
        CurrentPage = item.Title;
        var parameters = new NavigationParameters
        {
            { "title", item.Title },
            { "description", item.Description },
            { "icon", item.Icon }
        };
        _regionManager.RequestNavigate(
            NavigationCatalog.ContentRegion,
            item.Route,
            result =>
            {
                if (result.Result == true)
                {
                    Logger.Debug("页面导航成功。Route={0}", item.Route);
                    return;
                }
                Logger.Error(result.Error, "页面导航失败。Route={0}", item.Route);
                CurrentPage = $"{item.Title} · 页面加载失败";
                foreach (var navigationItem in NavigationItems) navigationItem.IsSelected = false;
            },
            parameters);
    }

    private void ToggleTheme()
    {
        _themeService.Toggle();
        RaisePropertyChanged(nameof(ThemeLabel));
        RaisePropertyChanged(nameof(ThemeGlyph));
    }

    public void SelectExternalWindow(NavigationItem item)
    {
        foreach (var navigationItem in NavigationItems)
            navigationItem.IsSelected = navigationItem.Route == item.Route;
        CurrentPage = $"{item.Title} · 独立窗口";
    }
}
