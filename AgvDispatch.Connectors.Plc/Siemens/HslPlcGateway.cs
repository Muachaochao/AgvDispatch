using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;
using HslCommunication;
using HslCommunication.Profinet.Siemens;
using System.Text.RegularExpressions;

namespace AgvDispatch.Connectors.Plc;

/// <summary>
/// 基于HslCommunication的西门子S7通信边界。点位由CSV提供，业务层不依赖HSL地址。
/// </summary>
public sealed partial class HslPlcGateway : IPlcGateway, IPlcBatchReader, IDisposable
{
    private static readonly object AuthorizationSync = new();
    private static string? _initializedAuthorizationCode;

    private readonly PlcOptions _options;
    private readonly PlcSignalCatalog _catalog;
    private readonly PlcConnectionSupervisor _supervisor;
    private readonly SiemensS7Net? _client;
    private readonly SemaphoreSlim _communicationLock = new(1, 1);
    private readonly string? _startupError;
    private bool _connected;
    private bool _disposed;

    public HslPlcGateway(
        PlcOptions options,
        PlcSignalCatalog catalog,
        PlcConnectionSupervisor supervisor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

        if (!_options.Enabled)
        {
            return;
        }

        _startupError = _options.GetConfigurationError();
        if (_startupError is not null)
        {
            _supervisor.ReportFault(_startupError);
            return;
        }

        var authorizationCode = Environment.GetEnvironmentVariable(
            _options.AuthorizationCodeEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(authorizationCode) && OperatingSystem.IsWindows())
        {
            authorizationCode = Environment.GetEnvironmentVariable(
                _options.AuthorizationCodeEnvironmentVariable,
                EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrWhiteSpace(authorizationCode))
        {
            _startupError = $"未设置环境变量{_options.AuthorizationCodeEnvironmentVariable}。";
            _supervisor.ReportFault(_startupError);
            return;
        }

        if (!InitializeAuthorization(authorizationCode))
        {
            _startupError = "HSL授权码校验失败。";
            _supervisor.ReportFault(_startupError);
            return;
        }

        if (!Enum.TryParse<SiemensPLCS>(_options.SiemensSeries, ignoreCase: true, out var series))
        {
            _startupError = $"无法识别西门子PLC系列：{_options.SiemensSeries}。";
            _supervisor.ReportFault(_startupError);
            return;
        }

        _client = new SiemensS7Net(series, _options.IpAddress)
        {
            Port = _options.Port,
            ConnectTimeOut = _options.ConnectTimeoutMs,
            ReceiveTimeOut = _options.ReceiveTimeoutMs
        };

        if (_options.Rack.HasValue)
        {
            _client.Rack = _options.Rack.Value;
        }

        if (_options.Slot.HasValue)
        {
            _client.Slot = _options.Slot.Value;
        }

        if (_options.ConnectionType.HasValue)
        {
            _client.ConnectionType = _options.ConnectionType.Value;
        }

        if (_options.LocalTsap.HasValue)
        {
            _client.LocalTSAP = _options.LocalTsap.Value;
        }

        if (_options.DestTsap.HasValue)
        {
            _client.DestTSAP = _options.DestTsap.Value;
        }
    }

    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _supervisor.Current;
        return Task.FromResult(new DeviceConnectionStatus(
            current.DeviceId,
            current.DisplayName,
            current.State,
            current.UpdatedAt,
            current.Message));
    }

    public async Task<IReadOnlyList<PlcSignalReading>> ReadEnabledSignalsAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException(_startupError);
        }

        var points = _catalog.Signals
            .Where(signal => signal.Enabled)
            .Select(ParseBooleanPoint)
            .ToArray();

        return await ExecuteAsync(async client =>
        {
            var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in points.GroupBy(point => (
                         point.DbNumber,
                         BlockStart: point.ByteOffset / _options.ReadBlockSizeBytes *
                                     _options.ReadBlockSizeBytes)))
            {
                var lastByte = block.Max(point => point.ByteOffset);
                var length = checked((ushort)(lastByte - block.Key.BlockStart + 1));
                var address = $"DB{block.Key.DbNumber}.{block.Key.BlockStart}";
                var result = await client.ReadAsync(address, length).WaitAsync(cancellationToken);
                EnsureSuccess(result, $"批量读取{address}，长度{length}字节");

                foreach (var point in block)
                {
                    var localOffset = point.ByteOffset - block.Key.BlockStart;
                    if (result.Content is null || localOffset >= result.Content.Length)
                    {
                        throw new InvalidDataException(
                            $"PLC返回数据长度不足，无法解析{point.Definition.Address}。");
                    }

                    var bitValue = (result.Content[localOffset] & (1 << point.BitOffset)) != 0;
                    values[CreateKey(point.Definition.StationId, point.Definition.VariableName)] = bitValue;
                }
            }

            var readAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow();
            return (IReadOnlyList<PlcSignalReading>)points.Select(point => new PlcSignalReading(
                point.Definition.StationId,
                point.Definition.VariableName,
                values[CreateKey(point.Definition.StationId, point.Definition.VariableName)],
                readAt)).ToArray();
        }, cancellationToken);
    }

    public async Task<bool?> ReadSignalAsync(
        string stationId,
        string signalName,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _startupError is not null)
        {
            return null;
        }

        if (!TryResolveSignal(stationId, signalName, "Read", out var signal))
        {
            return null;
        }

        EnsureBoolean(signal);
        return await ExecuteAsync(async client =>
        {
            var result = await client.ReadBoolAsync(signal.Address).WaitAsync(cancellationToken);
            EnsureSuccess(result, $"读取{stationId}/{signalName}");
            return result.Content;
        }, cancellationToken);
    }

    public async Task<StationSignal?> ReadStationAsync(
        string stationId,
        CancellationToken cancellationToken)
    {
        if (!_catalog.ContainsStation(stationId))
        {
            return null;
        }

        var fullBoxRequested = await ReadSignalAsync(
            stationId, "FullBoxRequested", cancellationToken) ?? false;
        var boxPresent = await ReadSignalAsync(
            stationId, "BoxPresent", cancellationToken) ?? false;
        var faulted = await ReadSignalAsync(
            stationId, "Faulted", cancellationToken) ?? false;
        var communicationOnline = await ReadSignalAsync(
            stationId, "CommunicationOnline", cancellationToken) ?? _connected;

        return new StationSignal(
            stationId,
            fullBoxRequested,
            boxPresent,
            faulted,
            communicationOnline,
            AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow());
    }

    public async Task WriteSignalAsync(
        string stationId,
        string signalName,
        bool value,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("PLC通信未启用，禁止写入现场信号。");
        }

        if (_startupError is not null)
        {
            throw new InvalidOperationException(_startupError);
        }

        if (!_options.AllowWrites)
        {
            throw new InvalidOperationException("PLC写入安全开关未开启（Plc:AllowWrites=false）。");
        }

        if (!TryResolveSignal(stationId, signalName, "Write", out var signal))
        {
            throw new KeyNotFoundException($"未找到已启用的PLC输出点位：{stationId}/{signalName}。");
        }

        EnsureBoolean(signal);
        await ExecuteAsync(async client =>
        {
            var writeResult = await client.WriteAsync(signal.Address, value).WaitAsync(cancellationToken);
            EnsureSuccess(writeResult, $"写入{stationId}/{signalName}");

            if (_options.VerifyWrites)
            {
                var readBack = await client.ReadBoolAsync(signal.Address).WaitAsync(cancellationToken);
                EnsureSuccess(readBack, $"回读{stationId}/{signalName}");
                if (readBack.Content != value)
                {
                    throw new InvalidOperationException(
                        $"PLC点位回读不一致：{stationId}/{signalName}，期望{value}，实际{readBack.Content}。");
                }
            }

            return true;
        }, cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        Func<SiemensS7Net, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _communicationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            return await operation(_client!);
        }
        catch
        {
            ResetConnection();
            throw;
        }
        finally
        {
            _communicationLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected)
        {
            return;
        }

        _supervisor.ReportConnecting();
        var result = await _client!.ConnectServerAsync().WaitAsync(cancellationToken);
        EnsureSuccess(result, "连接PLC");
        _connected = true;
    }

    private bool TryResolveSignal(
        string stationId,
        string signalName,
        string access,
        out PlcSignalDefinition signal)
    {
        if (!_catalog.TryGet(stationId, signalName, out signal!))
        {
            return false;
        }

        return signal.Enabled && string.Equals(signal.Access, access, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureBoolean(PlcSignalDefinition signal)
    {
        if (!string.Equals(signal.DataType, "Bool", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"变量{signal.StationId}/{signal.VariableName}的数据类型{signal.DataType}不能通过BOOL接口访问。");
        }
    }

    private static S7BooleanPoint ParseBooleanPoint(PlcSignalDefinition signal)
    {
        EnsureBoolean(signal);
        var match = S7BooleanAddressPattern().Match(signal.Address);
        if (!match.Success ||
            !int.TryParse(match.Groups["db"].Value, out var dbNumber) ||
            !int.TryParse(match.Groups["byte"].Value, out var byteOffset) ||
            !int.TryParse(match.Groups["bit"].Value, out var bitOffset))
        {
            throw new InvalidDataException(
                $"BOOL变量{signal.StationId}/{signal.VariableName}的S7地址无效：{signal.Address}。");
        }

        return new S7BooleanPoint(signal, dbNumber, byteOffset, bitOffset);
    }

    public static string? GetBooleanMappingError(PlcSignalDefinition signal)
    {
        try { _ = ParseBooleanPoint(signal); return null; }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        { return exception.Message; }
    }

    private static void EnsureSuccess(OperateResult result, string action)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"{action}失败（HSL错误码{result.ErrorCode}）：{result.Message}");
        }
    }

    private static bool InitializeAuthorization(string authorizationCode)
    {
        lock (AuthorizationSync)
        {
            if (string.Equals(_initializedAuthorizationCode, authorizationCode, StringComparison.Ordinal))
            {
                return true;
            }

            if (!Authorization.SetAuthorizationCode(authorizationCode))
            {
                return false;
            }

            _initializedAuthorizationCode = authorizationCode;
            return true;
        }
    }

    private void ResetConnection()
    {
        _connected = false;
        try
        {
            _client?.ConnectClose();
        }
        catch
        {
            // 断线清理不能覆盖原始通信异常；下一次请求会重新连接。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetConnection();
        _communicationLock.Dispose();
    }

    private static string CreateKey(string stationId, string variableName) =>
        $"{stationId}\u001F{variableName}";

    [GeneratedRegex(
        "^DB(?<db>[0-9]+)\\.DBX(?<byte>[0-9]+)\\.(?<bit>[0-7])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex S7BooleanAddressPattern();

    private sealed record S7BooleanPoint(
        PlcSignalDefinition Definition,
        int DbNumber,
        int ByteOffset,
        int BitOffset);
}
