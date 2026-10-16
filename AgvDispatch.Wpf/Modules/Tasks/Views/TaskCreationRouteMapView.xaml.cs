using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgvDispatch.Wpf.ViewModels;
namespace AgvDispatch.Wpf.Views;
public partial class TaskCreationRouteMapView:UserControl
{
    public TaskCreationRouteMapView()=>InitializeComponent();
    private void RouteClick(object sender,MouseButtonEventArgs e)
    {
        if(sender is FrameworkElement{DataContext:TaskCreationRouteMapStroke stroke}&&DataContext is TaskCenterViewModel vm){vm.LocateCreationRouteSegment(stroke.Segment);e.Handled=true;}
    }
}
