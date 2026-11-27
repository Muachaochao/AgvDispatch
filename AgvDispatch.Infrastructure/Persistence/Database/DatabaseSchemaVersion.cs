namespace AgvDispatch.Infrastructure.Persistence;

public static class DatabaseSchemaVersion
{
    // Bump only together with an idempotent migration in DatabaseInitializer.
    public const int Current = 1;
}
