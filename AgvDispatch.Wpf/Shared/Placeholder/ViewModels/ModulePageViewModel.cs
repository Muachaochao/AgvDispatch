using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class ModulePageViewModel : BindableBase, INavigationAware
{
    private string _title = "功能模块";
    private string _description = "此页面为功能占位，尚未接入业务数据。";
    private string _icon = "/AgvDispatch.Wpf;component/Assets/Icons/Png/overview.png";
    private string _primaryMetric = "—";
    private string _primaryLabel = "业务数据";
    private string _secondaryMetric = "占位页面";
    private string _secondaryLabel = "页面状态";
    private IReadOnlyList<ModuleFeatureItem> _features = [];

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public string Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    public string PrimaryMetric { get => _primaryMetric; private set => SetProperty(ref _primaryMetric, value); }
    public string PrimaryLabel { get => _primaryLabel; private set => SetProperty(ref _primaryLabel, value); }
    public string SecondaryMetric { get => _secondaryMetric; private set => SetProperty(ref _secondaryMetric, value); }
    public string SecondaryLabel { get => _secondaryLabel; private set => SetProperty(ref _secondaryLabel, value); }
    public IReadOnlyList<ModuleFeatureItem> Features { get => _features; private set => SetProperty(ref _features, value); }

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        Title = navigationContext.Parameters.GetValue<string>("title") ?? Title;
        Description = navigationContext.Parameters.GetValue<string>("description") ?? Description;
        Icon = navigationContext.Parameters.GetValue<string>("icon") ?? Icon;
        ConfigurePlaceholder();
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) { }

    private void ConfigurePlaceholder()
    {
        PrimaryLabel = "业务数据";
        PrimaryMetric = "—";
        SecondaryLabel = "页面状态";
        SecondaryMetric = "占位页面";
        Features = [new("页面说明", "尚未接入业务功能，当前不提供数据查询或设备操作。", "INFO")];
    }

}

public sealed record ModuleFeatureItem(string Title, string Description, string Tag);
