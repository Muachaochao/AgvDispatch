using Microsoft.Extensions.Logging;

internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        if (exception is not null) Console.Error.WriteLine(exception);
    }
}
