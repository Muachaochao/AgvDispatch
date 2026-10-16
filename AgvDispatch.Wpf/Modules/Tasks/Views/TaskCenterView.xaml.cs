using System.Windows;
using System.Windows.Controls;

namespace AgvDispatch.Wpf.Views;

public partial class TaskCenterView : UserControl
{
    private Window? _routeMapWindow;
    public TaskCenterView(){InitializeComponent();Unloaded+=(_,_)=>_routeMapWindow?.Close();}
    private void ShowCreationRouteMap(object sender,RoutedEventArgs e)
    {
        if(_routeMapWindow is not null){_routeMapWindow.Activate();return;}
        _routeMapWindow=new Window{Title="当前任务路线地图 · 只读观察",Content=new TaskCreationRouteMapView{DataContext=DataContext},Width=1040,Height=680,MinWidth=760,MinHeight=500,Background=(System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        if(Window.GetWindow(this) is {} owner)_routeMapWindow.Owner=owner;
        _routeMapWindow.Closed+=(_,_)=>_routeMapWindow=null;_routeMapWindow.Show();
    }
}
