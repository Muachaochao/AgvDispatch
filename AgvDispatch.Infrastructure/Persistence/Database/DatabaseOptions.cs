namespace AgvDispatch.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public string DatabasePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "data", "agv-dispatch.db");
}
