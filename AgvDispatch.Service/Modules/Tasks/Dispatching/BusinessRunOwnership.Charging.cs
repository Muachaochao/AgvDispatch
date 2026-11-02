namespace AgvDispatch.Service;

public sealed partial class BusinessRunOwnership
{
    private sealed record ChargingReturn(string VehicleId, string Site);
    private ChargingReturn? chargingReturn;

    // A paused plan supplies the charging return destination without retaining
    // the business execution ownership that would prevent charging itself.
    public string? GetChargingReturnSite(string vehicleId)
    {
        var current = Volatile.Read(ref chargingReturn);
        return current is not null && current.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase)
            ? current.Site : null;
    }

    internal void SetChargingReturnSite(string? vehicleId, string? site) =>
        Volatile.Write(ref chargingReturn, vehicleId is not null && site is not null
            ? new ChargingReturn(vehicleId, site) : null);
}
