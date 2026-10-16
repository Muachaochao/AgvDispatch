namespace AgvDispatch.Wpf.Services;

public interface IUserFeedback
{
    void Success(string message);
    void Warning(string message);
    void Error(string message);
}
