using System.Windows;

namespace AgvDispatch.Wpf.Services;

public sealed class ThemeService : IThemeService
{
    private const string LightTheme = "Themes/Light.xaml";
    private const string DarkTheme = "Themes/Dark.xaml";

    public bool IsDark { get; private set; }

    public void Toggle()
    {
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(x =>
            x.Source?.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
            x.Source?.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);

        if (current is null)
        {
            return;
        }

        var index = dictionaries.IndexOf(current);
        IsDark = !IsDark;
        // HandyControl controls, dropdown popups, Growl and MessageBox must switch
        // with the application palette, not keep their initial light skin.
        var skin = dictionaries.FirstOrDefault(x =>
            x.Source?.OriginalString.Contains("HandyControl;component/Themes/Skin", StringComparison.OrdinalIgnoreCase) == true);
        if (skin is not null)
            dictionaries[dictionaries.IndexOf(skin)] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/HandyControl;component/Themes/{(IsDark ? "SkinDark" : "SkinDefault")}.xaml", UriKind.Absolute)
            };
        dictionaries[index] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/AgvDispatch.Wpf;component/{(IsDark ? DarkTheme : LightTheme)}", UriKind.Absolute)
        };
        // Resource brushes may have captured their original lookup context.
        // Recreate the adapter so detached popups and dialogs receive new brushes too.
        var adapter = dictionaries.FirstOrDefault(x =>
            x.Source?.OriginalString.EndsWith("Themes/HandyPalette.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (adapter is not null)
            dictionaries[dictionaries.IndexOf(adapter)] = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/HandyPalette.xaml", UriKind.Absolute)
            };
    }
}
