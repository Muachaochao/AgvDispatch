using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

public sealed class SwitchVehicleCache : IAgvStateCache
{
    public List<AgvRobotSnapshot> Items { get; } = [];
    public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } }
    public long Version => 1;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => Items.ToArray();
    public bool TryGet(string id, out AgvRobotSnapshot snapshot)
    { snapshot = Items.FirstOrDefault(item => item.VehicleId == id)!; return snapshot is not null; }
    public static AgvRobotSnapshot Ready(string id = "AGV-01") => new(
        VehicleId: id, DisplayName: id, IpAddress: "127.0.0.1", Port: 19204,
        Connection: ConnectionState.Online, Quality: SignalQuality.Good,
        X: 0, Y: 0, Angle: 0, LocalizationConfidence: 0.95, CurrentStation: "SITE-A", LastStation: "SITE-A",
        BatteryPercent: 90, Blocked: false, EmergencyStopped: false, NavigationState: "0",
        SourceStation: null, TargetStation: null, PathPercentage: null, Tasks: [], Alarms: [],
        LastSuccessfulPollAt: DateTimeOffset.Now, UpdatedAt: DateTimeOffset.Now,
        ConsecutiveFailures: 0, LastResponseTimeMs: 1, ErrorMessage: null, Version: 1)
        { VelocityX = 0, VelocityY = 0, AngularVelocity = 0 };
}

public sealed class SwitchSignalCache : IPlcSignalCache
{
    public List<PlcSignalSnapshot> Items { get; } = [];
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } }
    public long Version => 1;
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => Items.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string id) => Items.Where(item => item.StationId == id).ToArray();
    public bool TryGet(string id, string name, out PlcSignalSnapshot snapshot)
    { snapshot = Items.FirstOrDefault(item => item.StationId == id && item.VariableName == name)!; return snapshot is not null; }
    public static PlcSignalSnapshot Ready() => new("PLC-01", "Faulted", "故障", "DB1.DBX0.0", "Bool",
        "PlcToHost", "Read", true, false, SignalQuality.Good, DateTimeOffset.Now, DateTimeOffset.Now, 1);
}
