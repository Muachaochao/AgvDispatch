using System.Windows;

namespace AgvDispatch.Wpf.Management;

public partial class ManagementApp : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var index = Array.IndexOf(e.Args, "--site-root");
            var site = SiteEnvironment.Find(index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null);
            MainWindow = new ManagementWindow(new ManagementViewModel(site));
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法打开现场管理工具：{exception.Message}\n\n请从发布包根目录启动，或使用 --site-root 指定包含最新版Web.json的目录。", "AGV 现场管理工具", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
