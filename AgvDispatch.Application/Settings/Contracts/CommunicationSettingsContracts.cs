using AgvDispatch.Domain.Devices;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AgvDispatch.Application.Abstractions;

public abstract class ObservableConnectionSetting : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public sealed class PlcConnectionSetting : ObservableConnectionSetting
{
    private string _deviceId = "PLC-NEW";
    public string DeviceId { get => _deviceId; set => Set(ref _deviceId, value); }
    private string _displayName = "PLC";
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private string _siemensSeries = "S1200";
    public string SiemensSeries { get => _siemensSeries; set => Set(ref _siemensSeries, value); }
    private string _ipAddress = "";
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
    private int _port = 102;
    public int Port { get => _port; set => Set(ref _port, value); }
    private byte? _rack;
    public byte? Rack { get => _rack; set => Set(ref _rack, value); }
    private byte? _slot;
    public byte? Slot { get => _slot; set => Set(ref _slot, value); }
    private int? _localTsap;
    public int? LocalTsap { get => _localTsap; set => Set(ref _localTsap, value); }
    private int? _destTsap;
    public int? DestTsap { get => _destTsap; set => Set(ref _destTsap, value); }
    private byte? _connectionType;
    public byte? ConnectionType { get => _connectionType; set => Set(ref _connectionType, value); }
    private int _pollIntervalMs = 500;
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, value); }
    private int _connectTimeoutMs = 3000;
    public int ConnectTimeoutMs { get => _connectTimeoutMs; set => Set(ref _connectTimeoutMs, value); }
    private int _receiveTimeoutMs = 3000;
    public int ReceiveTimeoutMs { get => _receiveTimeoutMs; set => Set(ref _receiveTimeoutMs, value); }
    private int _reconnectDelayMs = 3000;
    public int ReconnectDelayMs { get => _reconnectDelayMs; set => Set(ref _reconnectDelayMs, value); }
    private int _staleAfterMs = 5000;
    public int StaleAfterMs { get => _staleAfterMs; set => Set(ref _staleAfterMs, value); }
    private int _interruptedFailureThreshold = 3;
    public int InterruptedFailureThreshold { get => _interruptedFailureThreshold; set => Set(ref _interruptedFailureThreshold, value); }
    private int _readBlockSizeBytes = 200;
    public int ReadBlockSizeBytes { get => _readBlockSizeBytes; set => Set(ref _readBlockSizeBytes, value); }
    private string _signalCsvPath = "";
    public string SignalCsvPath { get => _signalCsvPath; set => Set(ref _signalCsvPath, value); }
}
public sealed class AgvConnectionSetting : ObservableConnectionSetting
{
    private string _vehicleId = "AGV-NEW";
    public string VehicleId { get => _vehicleId; set => Set(ref _vehicleId, value); }
    private string _displayName = "AGV";
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private string _ipAddress = "";
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
    private int _navigationPort = 19206;
    public int NavigationPort { get => _navigationPort; set => Set(ref _navigationPort, value); }
    private int _port = 19204;
    public int Port { get => _port; set => Set(ref _port, value); }
    private int? _otherApiPort;
    public int? OtherApiPort { get => _otherApiPort; set => Set(ref _otherApiPort, value); }
}
public sealed class AgvMapBindingSetting : ObservableConnectionSetting
{
    private string _vehicleId = "";
    public string VehicleId { get => _vehicleId; set => Set(ref _vehicleId, value); }
    private string _localMapId = "";
    public string LocalMapId { get => _localMapId; set => Set(ref _localMapId, value); }
    private string _localMapVersion = "";
    public string LocalMapVersion { get => _localMapVersion; set => Set(ref _localMapVersion, value); }
    private string _robotMap = "";
    public string RobotMap { get => _robotMap; set => Set(ref _robotMap, value); }
    private string _robotMapMd5 = "";
    public string RobotMapMd5 { get => _robotMapMd5; set => Set(ref _robotMapMd5, value); }
    private string _confirmationNote = "";
    public string ConfirmationNote { get => _confirmationNote; set => Set(ref _confirmationNote, value); }
    private bool _coordinatesVerified;
    public bool CoordinatesVerified {get=>_coordinatesVerified;set=>Set(ref _coordinatesVerified,value);}
}
public sealed class ChargerConnectionSetting : ObservableConnectionSetting
{
    private string _id = "";
    public string Id { get => _id; set => Set(ref _id, value); }
    private string _displayName = "";
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private string _ipAddress = "";
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
    private int _ackPort = 20207;
    public int AckPort { get => _ackPort; set => Set(ref _ackPort, value); }
    private int _statusPort = 20204;
    public int StatusPort { get => _statusPort; set => Set(ref _statusPort, value); }
}
public sealed class ChargingSiteSetting : ObservableConnectionSetting
{
    private string _id = "";
    public string Id { get => _id; set => Set(ref _id, value); }
    private string _chargerId = "";
    public string ChargerId { get => _chargerId; set => Set(ref _chargerId, value); }
    private string _chargingSiteId = "";
    public string ChargingSiteId { get => _chargingSiteId; set => Set(ref _chargingSiteId, value); }
    private string _exitSiteId = "";
    public string ExitSiteId { get => _exitSiteId; set => Set(ref _exitSiteId, value); }
    private string _resourceId = "";
    public string ResourceId { get => _resourceId; set => Set(ref _resourceId, value); }
    private string _controlMode = "RobokitScript";
    public string ControlMode { get => _controlMode; set => Set(ref _controlMode, value); }
    private bool _commissioned;
    public bool Commissioned { get => _commissioned; set => Set(ref _commissioned, value); }
    private List<string> _allowedVehicleIds = [];
    public List<string> AllowedVehicleIds { get => _allowedVehicleIds; set => Set(ref _allowedVehicleIds, value); }
}
public sealed class CommunicationConfiguration : ObservableConnectionSetting
{
    public List<AgvMapBindingSetting>? MapBindings { get; set; }
    private List<PlcConnectionSetting> _plcs = [];
    public List<PlcConnectionSetting> Plcs { get => _plcs; set => Set(ref _plcs, value); }
    private List<AgvConnectionSetting> _agvs = [];
    public List<AgvConnectionSetting> Agvs { get => _agvs; set => Set(ref _agvs, value); }
    private bool _chargerMonitoringEnabled = true;
    public bool ChargerMonitoringEnabled { get => _chargerMonitoringEnabled; set => Set(ref _chargerMonitoringEnabled, value); }
    private List<ChargerConnectionSetting> _chargers = [];
    public List<ChargerConnectionSetting> Chargers { get => _chargers; set => Set(ref _chargers, value); }
    private List<ChargingSiteSetting> _chargingSites = [];
    public List<ChargingSiteSetting> ChargingSites { get => _chargingSites; set => Set(ref _chargingSites, value); }
    public bool ChargerConfigurationInitialized { get; set; }
    public bool ChargingSiteConfigurationInitialized { get; set; }
    private bool _agvEnabled;
    public bool AgvEnabled { get => _agvEnabled; set => Set(ref _agvEnabled, value); }
    private byte _agvProtocolVersion = 1;
    public byte AgvProtocolVersion { get => _agvProtocolVersion; set => Set(ref _agvProtocolVersion, value); }
    private int _agvPollIntervalMs = 500;
    public int AgvPollIntervalMs { get => _agvPollIntervalMs; set => Set(ref _agvPollIntervalMs, value); }
    private int _agvConnectTimeoutMs = 3000;
    public int AgvConnectTimeoutMs { get => _agvConnectTimeoutMs; set => Set(ref _agvConnectTimeoutMs, value); }
    private int _agvRequestTimeoutMs = 3000;
    public int AgvRequestTimeoutMs { get => _agvRequestTimeoutMs; set => Set(ref _agvRequestTimeoutMs, value); }
    private int _agvReconnectDelayMs = 3000;
    public int AgvReconnectDelayMs { get => _agvReconnectDelayMs; set => Set(ref _agvReconnectDelayMs, value); }
    private int _agvStaleAfterMs = 5000;
    public int AgvStaleAfterMs { get => _agvStaleAfterMs; set => Set(ref _agvStaleAfterMs, value); }
}
public sealed record CommunicationSettingsAudit(DateTimeOffset At, string Operator, string Summary);
public sealed record CommunicationSettingsSnapshot(string Revision, CommunicationConfiguration Saved,
    CommunicationConfiguration Active, bool RestartRequired, bool CanManage,
    IReadOnlyList<PlcConnectionSnapshot> PlcConnections, IReadOnlyList<CommunicationSettingsAudit> Audits)
{
    public string? RuntimeMapId {get;init;}
    public string? RuntimeMapVersion {get;init;}
}
public sealed record SaveCommunicationSettingsRequest(string ExpectedRevision, CommunicationConfiguration Configuration);
public sealed record SetVehicleConnectionRequest(string VehicleId, bool Enabled, string ExpectedRevision);
public sealed record ConnectionProbeRequest(string IpAddress, int Port);
public sealed record ConnectionProbeResult(bool Reachable, double ElapsedMs, string Message);
public interface ICommunicationSettingsService
{
    Task<CommunicationSettingsSnapshot> SetVehicleConnectionAsync(TrafficUserContext user, SetVehicleConnectionRequest request, CancellationToken ct)
        => throw new NotSupportedException("后台尚未支持车辆即时连接管理。");
    Task<CommunicationSettingsSnapshot> GetAsync(TrafficUserContext user, CancellationToken cancellationToken);
    Task<CommunicationSettingsSnapshot> SaveAsync(TrafficUserContext user, SaveCommunicationSettingsRequest request, CancellationToken cancellationToken);
    Task<ConnectionProbeResult> ProbeAsync(TrafficUserContext user, ConnectionProbeRequest request, CancellationToken cancellationToken);
}
