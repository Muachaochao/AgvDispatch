using System.Windows;
using HandyControl.Controls;

namespace AgvDispatch.Wpf.Services;

// Only explicit user operations notify; polling/reconnect never create toasts.
public sealed class HandyUserFeedback : IUserFeedback
{
    public const string Token = "ShellNotifications";
    public void Success(string message) => OnUi(() => Growl.Success(message, Token));
    public void Warning(string message) => OnUi(() => Growl.Warning(message, Token));
    public void Error(string message) => OnUi(() => Growl.Error(message, Token));
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
