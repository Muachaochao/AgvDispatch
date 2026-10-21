using System.Windows;

namespace PlcHandshakeLab;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = e.Args.Contains("--network") || e.Args.Contains("--network-smoke-test") ? new NetworkWindow() : new MainWindow();
        MainWindow.Show();
    }
}
