using System.Windows;

namespace PlcHandshakeLab;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = e.Args.Any(argument =>
            argument.Equals("--network", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("--network-auto-serve", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("--network-smoke-test", StringComparison.OrdinalIgnoreCase))
            ? new NetworkWindow()
            : new MainWindow();
        MainWindow.Show();
    }
}
