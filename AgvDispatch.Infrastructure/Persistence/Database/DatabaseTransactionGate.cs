using System.Collections.Concurrent;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

/// <summary>
/// SQLite allows one writer at a time. SqlSugarScope hands out a scoped client per
/// async flow, but overlapping transactions from parallel workers can still share
/// the same physical connection and roll each other back. Keep only the short
/// database transaction inside this gate; vendor network calls stay parallel.
/// </summary>
public static class DatabaseTransactionGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static async ValueTask<IAsyncDisposable> EnterAsync(
        ISqlSugarClient database,
        CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(Key(database), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    public static async Task<DbResult<T>> RunIsolatedAsync<T>(
        ISqlSugarClient database,
        Func<ISqlSugarClient, Task<DbResult<T>>> action,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isolated = DatabaseFactory.CreateIsolated(database);
        try
        {
            return await action(isolated).ConfigureAwait(false);
        }
        finally
        {
            isolated.Dispose();
        }
    }

    private static string Key(ISqlSugarClient database) =>
        database.CurrentConnectionConfig?.ConnectionString ?? "default";

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
