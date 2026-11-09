namespace AgvDispatch.Application.Abstractions;

public sealed record AgvTransportChain(string BusinessTaskId, string VehicleId, IReadOnlyList<string> Path, int AttemptCount = 0, bool MaySend = true);
public interface IAgvTransportChainLookup
{
    Task<AgvTransportChain?> FindAsync(string businessTaskId, CancellationToken ct);
}
public sealed class AgvPartialAcceptanceException(string message) : InvalidOperationException(message);

/// <summary>
/// The local pre-send checks failed before any navigation write was attempted.
/// This attempt may be rolled back without weakening uncertain-send recovery.
/// </summary>
public sealed class AgvPreSendDeferredException(string message) : InvalidOperationException(message);

/// <summary>
/// Robokit explicitly rejected a navigation write before creating a task.
/// Unlike a timeout or partial acceptance, this result is terminal and must
/// never be retried under the same business id.
/// </summary>
public sealed class AgvCommandRejectedException(ushort requestType, int returnCode, string message)
    : InvalidOperationException(
        $"AGV_PRE_SEND_REJECTED; Robokit API {requestType}返回错误{returnCode}：{message}")
{
    public ushort RequestType { get; } = requestType;
    public int ReturnCode { get; } = returnCode;
}
