using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace AgvDispatch.Connectors.Agv;

internal sealed class SeerTcpChannel(string ipAddress, int port, AgvOptions options) : IAsyncDisposable
{
    private readonly string _ipAddress = ipAddress;
    private readonly int _port = port;
    private readonly AgvOptions _options = options;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _sequence;
    private long _lastRequest;
    private bool _disposed;
    public bool IsConnected => _client?.Connected == true && _stream is not null;
    public async Task<JsonDocument> RequestAsync(ushort messageType, object? payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lastRequest != 0)
            {
                var wait = _options.RequestIntervalMs - Stopwatch.GetElapsedTime(_lastRequest).TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
            }
            await EnsureConnectedAsync(cancellationToken);
            _lastRequest = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeoutMs);
            var token = timeout.Token;
            var payloadBytes = payload is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(payload);
            var sequence = unchecked((ushort)Interlocked.Increment(ref _sequence));
            var header = SeerProtocolCodec.CreateHeader(
                _options.ProtocolVersion,
                sequence,
                payloadBytes.Length,
                messageType);

            await _stream!.WriteAsync(header, token);
            await _stream.WriteAsync(payloadBytes, token);
            await _stream.FlushAsync(token);

            var responseHeaderBytes = new byte[SeerProtocolCodec.HeaderLength];
            await _stream.ReadExactlyAsync(responseHeaderBytes, token);
            var response = SeerProtocolCodec.ParseHeader(responseHeaderBytes);
            if (response.Version != _options.ProtocolVersion)
            {
                throw new InvalidDataException(
                    $"Robokit响应协议版本不一致，期望{_options.ProtocolVersion}，实际{response.Version}。");
            }

            if (response.Sequence != sequence)
            {
                throw new InvalidDataException(
                    $"Robokit响应序号不一致，期望{sequence}，实际{response.Sequence}。");
            }

            var expectedType = checked((ushort)(messageType + SeerProtocolCodec.ResponseOffset));
            if (response.MessageType != expectedType)
            {
                throw new InvalidDataException(
                    $"Robokit响应编号不一致，期望{expectedType}，实际{response.MessageType}。");
            }

            if (response.PayloadLength < 0 || response.PayloadLength > _options.MaximumFrameBytes)
            {
                throw new InvalidDataException(
                    $"Robokit响应数据区长度{response.PayloadLength}超过安全限制。");
            }

            var responsePayload = new byte[response.PayloadLength];
            if (responsePayload.Length > 0)
            {
                await _stream.ReadExactlyAsync(responsePayload, token);
            }

            var document = responsePayload.Length == 0
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(responsePayload);

            return document;
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // TcpClient.Connected reflects the last I/O. In a split-port setup the
        // status channel may recover while navigation still holds a peer-closed
        // socket. Observe an already-delivered FIN/RST before writing a command;
        // this is not a retry of a possibly-sent control request.
        if (IsConnected && !(_client!.Client.Poll(0, SelectMode.SelectRead) && _client.Available == 0))
        {
            return;
        }

        Disconnect();
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(_ipAddress, _port, timeout.Token);
            _client = client;
            _stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }


    public void Disconnect()
    {
        _stream?.Dispose(); _client?.Dispose(); _stream = null; _client = null;
    }
    public async ValueTask DisposeAsync()
    {
        await _requestLock.WaitAsync();
        try { _disposed = true; Disconnect(); }
        finally { _requestLock.Release(); }
    }
}
