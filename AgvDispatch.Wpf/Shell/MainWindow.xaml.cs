using System.Windows;
using System.Windows.Controls;
using AgvDispatch.Wpf.ViewModels;

namespace AgvDispatch.Wpf;

public partial class MainWindow : Window
{
    private bool _initialized;
    private MapEditorWindow? _mapEditorWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _initialized = true;
        viewModel.LoadedCommand.Execute();
    }

    private void NavigationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not Button { DataContext: NavigationItem item }) return;
        viewModel.NavigateCommand.Execute(item);
    }

    public void OpenMapEditorWindow(string? mapId = null)
    {
        if (_mapEditorWindow is { IsLoaded: true })
        {
            if (_mapEditorWindow.WindowState == WindowState.Minimized) _mapEditorWindow.WindowState = WindowState.Maximized;
            _mapEditorWindow.Activate();
            return;
        }
        _mapEditorWindow = new MapEditorWindow { Owner = this, InitialMapId = mapId };
        _mapEditorWindow.Closed += (_, _) => _mapEditorWindow = null;
        _mapEditorWindow.Show();
    }

}
