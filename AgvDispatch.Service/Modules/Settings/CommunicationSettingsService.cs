using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;

namespace AgvDispatch.Service;

public sealed record CommunicationSettingsDocument(int Version, string Revision,
    CommunicationConfiguration Configuration, IReadOnlyList<CommunicationSettingsAudit> Audits);

public sealed class CommunicationSettingsService : ICommunicationSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path, _baseDirectory;
    private readonly CommunicationConfiguration _active;
    private readonly PlcFleet _fleet;
    private readonly ILogger<CommunicationSettingsService> _logger;
    private readonly IActiveTaskLookup _tasks;
    private readonly IActiveMapRuntime? _maps;
    private CommunicationSettingsDocument _saved;

    public CommunicationSettingsService(string path, string baseDirectory, CommunicationConfiguration active,
        PlcFleet fleet, IActiveTaskLookup tasks, ILogger<CommunicationSettingsService> logger, IActiveMapRuntime? maps = null)
    {
        _path = path; _baseDirectory = baseDirectory; _active = active; _fleet = fleet; _logger = logger;
        _tasks = tasks; _maps=maps;
        _saved = ReadDocument(path) ?? new(1, "initial", active, []);
        _saved.Configuration.MapBindings ??= Clone(active).MapBindings ?? [];
    }

    public static CommunicationSettingsDocument? ReadDocument(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("通信配置文件过大。");
        var document = JsonSerializer.Deserialize<CommunicationSettingsDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("通信配置文件为空。");
        if (document.Version != 1) throw new InvalidDataException("不支持的通信配置版本。");
        return document;
    }

    public static bool OnlyMapBindingsDiffer(CommunicationConfiguration saved,CommunicationConfiguration proposed)
    {
        var copy=Clone(proposed);
        copy.MapBindings=Clone(saved).MapBindings;
        return JsonSerializer.Serialize(saved)==JsonSerializer.Serialize(copy);
    }

    public static CommunicationConfiguration FromLegacy(PlcOptions p, AgvOptions a) => new()
    {
        Plcs = [new() { DeviceId = p.DeviceId, DisplayName = p.DisplayName, Enabled = p.Enabled,
            SiemensSeries = p.SiemensSeries, IpAddress = p.IpAddress, Port = p.Port, Rack = p.Rack,
            Slot = p.Slot, LocalTsap = p.LocalTsap, DestTsap = p.DestTsap, ConnectionType = p.ConnectionType,
            PollIntervalMs = p.PollIntervalMs, ConnectTimeoutMs = p.ConnectTimeoutMs, ReceiveTimeoutMs = p.ReceiveTimeoutMs,
            ReconnectDelayMs = p.ReconnectDelayMs, StaleAfterMs = p.StaleAfterMs,
            InterruptedFailureThreshold = p.InterruptedFailureThreshold, ReadBlockSizeBytes = p.ReadBlockSizeBytes,
            SignalCsvPath = p.SignalCsvPath }],
        Agvs = a.Robots.Select(r => new AgvConnectionSetting { VehicleId = r.VehicleId, DisplayName = r.DisplayName,
            Enabled = r.Enabled, IpAddress = r.IpAddress, Port = r.Port, NavigationPort = r.NavigationPort }).ToList(),
        MapBindings = a.MapBindings.Select(b=>new AgvMapBindingSetting { VehicleId=b.VehicleId,LocalMapId=b.LocalMapId,LocalMapVersion=b.LocalMapVersion,RobotMap=b.RobotMap,RobotMapMd5=b.RobotMapMd5,CoordinatesVerified=b.CoordinatesVerified,ConfirmationNote=b.ConfirmationNote }).ToList(),
        AgvEnabled = a.Enabled, AgvProtocolVersion = a.ProtocolVersion, AgvPollIntervalMs = a.PollIntervalMs,
        AgvConnectTimeoutMs = a.ConnectTimeoutMs, AgvRequestTimeoutMs = a.RequestTimeoutMs,
        AgvReconnectDelayMs = a.ReconnectDelayMs, AgvStaleAfterMs = a.StaleAfterMs
    };

    public static string ResolveCsv(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var root = Path.GetFullPath(Path.Combine(baseDirectory, "config")) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(resolved), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PLC点表必须是后台config目录下的CSV文件。");
        return resolved;
    }
    public static void Validate(CommunicationConfiguration c, string baseDirectory)
    {
        if (c.Plcs is null || c.Agvs is null || c.Plcs.Count is < 1 or > 64 || c.Agvs.Count > 100)
            throw new ArgumentException("PLC配置需1~64项（可禁用），AGV最多100项。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in c.Plcs)
        {
            ValidateId(p.DeviceId);
            if (!ids.Add(p.DeviceId)) throw new ArgumentException($"PLC编号重复：{p.DeviceId}");
            if (!new[] { "S1200", "S1500", "S300", "S400", "S200Smart", "S200" }.Contains(p.SiemensSeries))
                throw new ArgumentException("西门子PLC系列无效。");
            ValidateEndpoint(p.IpAddress, p.Port, p.Enabled);
            if (p.Enabled && !endpoints.Add($"{IPAddress.Parse(p.IpAddress)}:{p.Port}")) throw new ArgumentException("启用的PLC连接地址重复。");
            if (p.DisplayName is null || p.DisplayName.Length > 80 || p.PollIntervalMs is < 200 or > 60000 ||
                p.ConnectTimeoutMs is < 500 or > 60000 || p.ReceiveTimeoutMs is < 500 or > 60000 ||
                p.ReconnectDelayMs is < 500 or > 60000 || p.StaleAfterMs < p.PollIntervalMs || p.StaleAfterMs > 300000 ||
                p.InterruptedFailureThreshold is < 2 or > 100 || p.ReadBlockSizeBytes is < 1 or > 2000 ||
                p.LocalTsap is < 0 or > 65535 || p.DestTsap is < 0 or > 65535)
                throw new ArgumentException($"PLC {p.DeviceId} 的周期、超时或S7参数无效。");
            var csv = ResolveCsv(p.SignalCsvPath, baseDirectory);
            if (p.Enabled && csv.Length == 0) throw new ArgumentException($"PLC {p.DeviceId} 启用前需绑定CSV点表。");
            if (csv.Length > 0)
            {
                var catalog = PlcSignalCatalog.Load(csv);
                if (p.Enabled && catalog.EnabledCount == 0) throw new ArgumentException($"PLC {p.DeviceId} 点表没有启用点位。");
                foreach (var station in catalog.Signals.Select(s => s.StationId).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!stations.Add(station)) throw new ArgumentException($"工位{station}重复绑定多个PLC；请拆分点表并使用唯一工位编号。");
            }
        }
        ids.Clear(); endpoints.Clear();
        foreach (var a in c.Agvs)
        {
            ValidateId(a.VehicleId);
            if (!ids.Add(a.VehicleId)) throw new ArgumentException($"AGV编号重复：{a.VehicleId}");
            ValidateEndpoint(a.IpAddress, a.Port, c.AgvEnabled && a.Enabled);
            ValidateEndpoint(a.IpAddress, a.NavigationPort, c.AgvEnabled && a.Enabled);
            if (a.DisplayName is null || a.DisplayName.Length > 80) throw new ArgumentException("AGV名称过长。");
            if (c.AgvEnabled && a.Enabled)
                foreach (var port in new[] { a.Port, a.NavigationPort }.Distinct())
                    if (!endpoints.Add($"{IPAddress.Parse(a.IpAddress)}:{port}")) throw new ArgumentException("不同AGV的状态或导航连接地址重复。");
        }
        if(c.MapBindings is { } bindings)
        {
            if(bindings.Count>500)throw new ArgumentException("地图绑定最多500项。");
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var b in bindings)
            {
                if(b is null || !c.Agvs.Any(v=>v.VehicleId==b.VehicleId) || string.IsNullOrWhiteSpace(b.LocalMapId) || b.LocalMapId.Length>64 ||
                    string.IsNullOrWhiteSpace(b.LocalMapVersion) || b.LocalMapVersion.Length>64 || string.IsNullOrWhiteSpace(b.RobotMap) || b.RobotMap.Length>128 ||
                    b.RobotMap.Any(ch=>!char.IsAscii(ch)||char.IsControl(ch)) || string.IsNullOrWhiteSpace(b.RobotMapMd5) || b.RobotMapMd5.Length!=32 || b.RobotMapMd5.Any(ch=>!Uri.IsHexDigit(ch)) ||
                    b.ConfirmationNote is null || b.ConfirmationNote.Length>1000)
                    throw new ArgumentException("地图绑定需已配置车辆、本地地图/版本、ASCII车端地图名和32位MD5；核对依据最多1000字。");
                if(!keys.Add(b.VehicleId+"/"+b.LocalMapId+"/"+b.LocalMapVersion))throw new ArgumentException("同车同地图版本的绑定重复。");
            }
        }
        if (c.AgvEnabled && !c.Agvs.Any(a => a.Enabled)) throw new ArgumentException("启用AGV采集前至少选择一台车。");
        if (c.AgvProtocolVersion is not (1 or 2) || c.AgvPollIntervalMs is < 200 or > 60000 ||
            c.AgvConnectTimeoutMs is < 500 or > 60000 || c.AgvRequestTimeoutMs is < 500 or > 60000 ||
            c.AgvReconnectDelayMs is < 500 or > 60000 || c.AgvStaleAfterMs < c.AgvPollIntervalMs || c.AgvStaleAfterMs > 300000)
            throw new ArgumentException("AGV公共协议版本、周期或超时无效。");
    }
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("设备编号仅允许英文字母、数字、下划线或连字符，最长32字符。");
    }
    private static void ValidateEndpoint(string ip, int port, bool required)
    {
        if (port is < 1 or > 65535 || ip is null || (required && ip.Length == 0) ||
            (ip.Length > 0 && (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)))
            throw new ArgumentException("连接需填写有效IPv4地址，端口范围1~65535。");
    }

    public static PlcOptions ToPlcOptions(PlcConnectionSetting p, PlcOptions legacy, string baseDirectory) => new()
    {
        Enabled = p.Enabled, AllowWrites = legacy.AllowWrites, VerifyWrites = legacy.VerifyWrites,
        AuthorizationCodeEnvironmentVariable = legacy.AuthorizationCodeEnvironmentVariable,
        DeviceId = p.DeviceId, DisplayName = p.DisplayName, SiemensSeries = p.SiemensSeries,
        IpAddress = p.IpAddress, Port = p.Port, Rack = p.Rack, Slot = p.Slot, ConnectionType = p.ConnectionType,
        LocalTsap = p.LocalTsap, DestTsap = p.DestTsap, PollIntervalMs = p.PollIntervalMs,
        ConnectTimeoutMs = p.ConnectTimeoutMs, ReceiveTimeoutMs = p.ReceiveTimeoutMs, ReconnectDelayMs = p.ReconnectDelayMs,
        StaleAfterMs = p.StaleAfterMs, InterruptedFailureThreshold = p.InterruptedFailureThreshold,
        ReadBlockSizeBytes = p.ReadBlockSizeBytes, SignalCsvPath = ResolveCsv(p.SignalCsvPath, baseDirectory)
    };
    public static void ApplyAgv(CommunicationConfiguration c, AgvOptions a)
    {
        a.Enabled = c.AgvEnabled; a.ProtocolVersion = c.AgvProtocolVersion; a.PollIntervalMs = c.AgvPollIntervalMs;
        a.ConnectTimeoutMs = c.AgvConnectTimeoutMs; a.RequestTimeoutMs = c.AgvRequestTimeoutMs;
        a.ReconnectDelayMs = c.AgvReconnectDelayMs; a.StaleAfterMs = c.AgvStaleAfterMs;
        if(c.MapBindings is not null) a.MapBindings=c.MapBindings.Select(b=>new RobokitMapBinding {VehicleId=b.VehicleId,LocalMapId=b.LocalMapId,LocalMapVersion=b.LocalMapVersion,RobotMap=b.RobotMap,RobotMapMd5=b.RobotMapMd5,CoordinatesVerified=b.CoordinatesVerified,ConfirmationNote=b.ConfirmationNote}).ToList();
        a.Robots = c.Agvs.Select(r => new AgvRobotOptions { VehicleId = r.VehicleId, DisplayName = r.DisplayName,
            Enabled = r.Enabled, IpAddress = r.IpAddress, Port = r.Port, NavigationPort = r.NavigationPort }).ToList();
    }

    private CommunicationSettingsSnapshot Snapshot(TrafficUserContext user) => new(_saved.Revision,
        Clone(_saved.Configuration), Clone(_active), JsonSerializer.Serialize(_saved.Configuration) != JsonSerializer.Serialize(_active),
        user.Has(TrafficPermission.ManageConnections), _fleet.Connections.Select(c => c.Supervisor.Current).ToArray(), _saved.Audits) {RuntimeMapId=_maps?.Current?.MapId,RuntimeMapVersion=_maps?.Current?.VersionId};
    private static CommunicationConfiguration Clone(CommunicationConfiguration value) =>
        JsonSerializer.Deserialize<CommunicationConfiguration>(JsonSerializer.Serialize(value))!;
    public async Task<CommunicationSettingsSnapshot> GetAsync(TrafficUserContext user, CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("无通信配置查看权限。");
        await _gate.WaitAsync(cancellationToken);
        try { return Snapshot(user); } finally { _gate.Release(); }
    }
    public async Task<CommunicationSettingsSnapshot> SaveAsync(TrafficUserContext user, SaveCommunicationSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.ManageConnections)) throw new UnauthorizedAccessException("无通信配置管理权限。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_saved.Revision != request.ExpectedRevision) throw new InvalidOperationException("配置已被其他人修改，请重新加载后编辑。");
            if ((await _tasks.GetActiveTasksAsync(cancellationToken)).Count > 0)
                throw new InvalidOperationException("存在未结束任务（含故障任务），请先完成安全处置再修改通信配置。");
            request.Configuration.MapBindings ??= Clone(_saved.Configuration).MapBindings ?? [];
            Validate(request.Configuration, _baseDirectory);
            foreach(var b in request.Configuration.MapBindings)
            {
                var old=_saved.Configuration.MapBindings?.SingleOrDefault(x=>x.VehicleId==b.VehicleId && x.LocalMapId==b.LocalMapId && x.LocalMapVersion==b.LocalMapVersion);
                var unchanged=old is not null && JsonSerializer.Serialize(old)==JsonSerializer.Serialize(b);
                if(b.CoordinatesVerified && !unchanged && string.IsNullOrWhiteSpace(b.ConfirmationNote))
                    throw new ArgumentException("勾选坐标已核定前须填写核对依据；软件不会自动确认坐标。");
            }
            var audit = new CommunicationSettingsAudit(DateTimeOffset.Now, user.WindowsIdentity,
                $"保存通信配置：PLC={request.Configuration.Plcs.Count}，AGV={request.Configuration.Agvs.Count}，地图绑定={request.Configuration.MapBindings.Count}；服务重启生效。");
            var saved = new CommunicationSettingsDocument(1, Guid.NewGuid().ToString("N"), request.Configuration,
                _saved.Audits.Prepend(audit).Take(100).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(saved, JsonOptions), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _saved = saved;
            _logger.LogInformation("通信配置已保存。Operator={Operator}, Revision={Revision}, Plcs={Plcs}, Agvs={Agvs}",
                user.WindowsIdentity, saved.Revision, saved.Configuration.Plcs.Count, saved.Configuration.Agvs.Count);
            return Snapshot(user);
        }
        finally { _gate.Release(); }
    }
    public async Task<ConnectionProbeResult> ProbeAsync(TrafficUserContext user, ConnectionProbeRequest request, CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.ManageConnections)) throw new UnauthorizedAccessException("无通信测试权限。");
        ValidateEndpoint(request.IpAddress, request.Port, true);
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(3000);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(request.IpAddress), request.Port, timeout.Token);
            return new(true, watch.Elapsed.TotalMilliseconds, "TCP端口可达；未发送协议报文，不代表S7授权、点表或AGV协议验证通过。");
        }
        catch (Exception error) when (error is SocketException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return new(false, watch.Elapsed.TotalMilliseconds, $"TCP连接失败或超时：{error.Message}"); }
    }
}
