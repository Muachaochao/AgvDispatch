namespace AgvDispatch.Application.Abstractions;

public sealed record AgvTransportChain(string BusinessTaskId, string VehicleId, IReadOnlyList<string> Path, int AttemptCount = 0, bool MaySend = true);
public interface IAgvTransportChainLookup
{
    Task<AgvTransportChain?> FindAsync(string businessTaskId, CancellationToken ct);
}
public sealed class AgvPartialAcceptanceException(string message) : InvalidOperationException(message);
