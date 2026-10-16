namespace AgvDispatch.Domain.Devices;

public enum ConnectionState
{
    Unknown = 0,
    Disconnected = 10,
    Connecting = 20,
    Online = 30,
    Degraded = 40,
    Interrupted = 50,
    Faulted = 60
}
