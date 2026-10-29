namespace AgvDispatch.Application.Abstractions;

public interface IChargingBusinessSafety
{
    Task<string?> GetBlockReasonAsync(string vehicleId,CancellationToken ct);
}
