using Prism.Mvvm;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class NavigationItem : BindableBase
{
    private bool _isSelected;

    public NavigationItem(string route, string title, string icon, string description)
    {
        Route = route;
        Title = title;
        Icon = icon;
        Description = description;
    }

    public string Route { get; }
    public string Title { get; }
    public string Icon { get; }
    public string Description { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
