namespace AgvDispatch.Domain.Devices;

public enum SignalQuality
{
    Unknown = 0,
    Good = 10,
    Stale = 20,
    Bad = 30,
    Disabled = 40
}
