namespace AgvDispatch.Application.Abstractions;

/// <summary>Reads the durable charging record without taking execution or charging
/// service locks. Recovery uses this independently of the charging runtime.</summary>
public interface IChargingRecoveryEvidence
{
    Task<ChargingSession?> ReadRecoverySessionAsync(string sessionId, CancellationToken cancellationToken);
}
