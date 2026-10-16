using System.Windows;
using System.Windows.Controls;

namespace AgvDispatch.Wpf.Views;

public partial class MapOverviewView : UserControl
{
    public MapOverviewView() => InitializeComponent();

    private void OpenEditor_OnClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.OpenMapEditorWindow((DataContext as AgvDispatch.Wpf.ViewModels.MapOverviewViewModel)?.SelectedMap?.MapId);
    }
}
