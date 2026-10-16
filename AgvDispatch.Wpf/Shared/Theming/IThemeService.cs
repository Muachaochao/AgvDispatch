namespace AgvDispatch.Wpf.Services;

public interface IThemeService
{
    bool IsDark { get; }
    void Toggle();
}
