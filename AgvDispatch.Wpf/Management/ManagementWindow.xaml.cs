using System.Windows;
using System.Windows.Threading;

namespace AgvDispatch.Wpf.Management;

public partial class ManagementWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    public ManagementWindow(ManagementViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Loaded += async (_, _) => { await viewModel.RefreshAsync(); await viewModel.ReadConnectionsAsync(); _timer.Start(); };
        _timer.Tick += async (_, _) => { await viewModel.RefreshAsync(); await viewModel.ReadConnectionsAsync(); };
        Closing += (_, e) =>
        {
            if (!viewModel.IsBusy) return;
            e.Cancel = true;
            MessageBox.Show("管理操作正在进行，请等待结果后再关闭窗口。", "AGV 软件助手", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        Closed += (_, _) => _timer.Stop();
    }
}
