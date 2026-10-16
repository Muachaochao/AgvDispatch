using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgvDispatch.Wpf.ViewModels;

namespace AgvDispatch.Wpf.Views;
public partial class ConnectionSettingsView : UserControl
{
    public ConnectionSettingsView() => InitializeComponent();
    private void ReviewSegmentLoaded(object sender, RoutedEventArgs e)
    {
        if(sender is DataGrid {SelectedItem: {} item} grid)grid.ScrollIntoView(item);
    }
    private void ReviewSegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(sender is not DataGrid grid || grid.SelectedItem is not {} item)return;
        grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>
        {if(ReferenceEquals(grid.SelectedItem,item))grid.ScrollIntoView(item);}));
    }
    private void ReviewRouteClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if(sender is FrameworkElement {DataContext:ReviewRouteOverlay {Segment: {} segment}} && DataContext is ConnectionSettingsViewModel vm)
        {vm.SelectReviewSegment(segment);e.Handled=true;}
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionSettingsViewModel vm) return;
        if (!PlcGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PlcGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !AgvGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !AgvGrid.CommitEdit(DataGridEditingUnit.Row, true) || HasError(this))
        { vm.ReportValidationError(); return; }
        if (!vm.SaveCommand.CanExecute()) return;
        var result = HandyControl.Controls.MessageBox.Show(Window.GetWindow(this),
            "保存将更新后台连接配置，安全停机并重启后台服务后生效。\n请确认PLC/AGV地址与采集开关；本操作不会自动启用写入、车辆动作或调度。",
            "保存连接配置", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result == MessageBoxResult.OK) vm.SaveCommand.Execute();
    }
    private static bool HasError(DependencyObject item)
        => HasError(item, new HashSet<DependencyObject>());
    private static bool HasError(DependencyObject item, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(item)) return false;
        if (Validation.GetHasError(item)) return true;
        if (item is Visual or System.Windows.Media.Media3D.Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
                if (HasError(VisualTreeHelper.GetChild(item, i), visited)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            if (HasError(child, visited)) return true;
        return false;
    }
}
