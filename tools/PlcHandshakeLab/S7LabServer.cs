using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace PlcHandshakeLab;

/// <summary>Bounded S7 ISO-on-TCP subset for BOOL/BYTE DB reads and writes.
/// Reuses the production lab's wire contract, with explicit P/H write ownership.</summary>
public sealed class S7LabServer : IAsyncDisposable
{
    private readonly NetworkLab _lab;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clients = new();
    private Task? _accept;
    private long _lastRequestTicks;
    public DateTimeOffset? LastRequest => Interlocked.Read(ref _lastRequestTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime() : null;
    public int Clients => _clients.Count;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string? Fault { get; private set; }

    public S7LabServer(NetworkLab lab)
    {
        _lab = lab;
        _listener = new TcpListener(IPAddress.Parse(lab.Config.BindAddress), lab.Config.Port);
    }
    public void Start()
    {
        _listener.Start(8);
        try { _lab.Note("监听", $"{_listener.LocalEndpoint}；允许调度IP={_lab.Config.AllowedClient}"); }
        catch { _listener.Stop(); throw; }
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                var peer = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!peer.Equals(IPAddress.Parse(_lab.Config.AllowedClient)) || _clients.Count >= 8)
                { client.Dispose(); _lab.Note("拒绝连接", peer.ToString()); continue; }
                _clients[client] = Task.CompletedTask;
                var task = ServeAsync(client);
                if (_clients.ContainsKey(client)) _clients[client] = task;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Fault = "监听停止：" + ex.Message; }
    }

    private static int U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), checked((ushort)value));
    private async Task ServeAsync(TcpClient client)
    {
        var peer = client.Client.RemoteEndPoint!.ToString()!;
        try
        {
            _lab.Note("连接", peer);
            using var stream = client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(30));
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, budget.Token).ConfigureAwait(false);
                var size = U16(header, 2);
                if (header[0] != 3 || header[1] != 0 || size is < 7 or > 8192) throw new InvalidDataException("TPKT帧无效或超长");
                var packet = new byte[size];
                header.CopyTo(packet, 0);
                await stream.ReadExactlyAsync(packet.AsMemory(4), budget.Token).ConfigureAwait(false);
                byte[] response;
                if (packet[5] == 0xe0)
                {
                    if (size < 18 || packet[4] != size - 5) throw new InvalidDataException("COTP连接请求无效");
                    response = packet.ToArray(); response[5] = 0xd0;
                    // Destination reference echoes the caller's source reference.
                    response[6] = packet[8]; response[7] = packet[9]; response[8] = 0; response[9] = 1;
                }
                else response = Reply(packet, peer);
                await stream.WriteAsync(response, budget.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastRequestTicks, DateTimeOffset.UtcNow.Ticks);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try { _lab.Note("协议/连接错误", peer + " " + ex.Message); }
            catch (Exception logError) { Fault = "日志写入失败：" + logError.Message; _stop.Cancel(); _listener.Stop(); }
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private byte[] Reply(byte[] packet, string peer)
    {
        if (packet.Length < 18 || packet[4] != 2 || packet[5] != 0xf0 || packet[6] != 0x80 || packet[7] != 0x32 || packet[8] != 1)
            throw new InvalidDataException("不支持的S7请求头");
        var plen = U16(packet, 13); var dlen = U16(packet, 15);
        if (plen < 1 || 17 + plen + dlen != packet.Length) throw new InvalidDataException("S7参数长度不匹配");
        var function = packet[17];
        byte[] parameters;
        using var output = new MemoryStream();
        if (function == 0xf0)
        {
            if (plen != 8 || dlen != 0) throw new InvalidDataException("无效的S7协商");
            parameters = [0xf0, 0, 0, 1, 0, 1, 3, 0xc0];
        }
        else if (function is 4 or 5)
        {
            if (plen < 2) throw new InvalidDataException("缺少变量数量");
            var count = packet[18];
            if (count is < 1 or > 64 || plen != 2 + count * 12 || function == 4 && dlen != 0)
                throw new InvalidDataException("变量项数量不匹配");
            parameters = [function, count];
            var cursor = 17 + plen;
            // Parse every variable and payload before applying any write.
            var items = new List<(int Kind, int Count, int Db, int Offset, int Bit, bool Valid, byte[] Raw)>();
            for (var i = 0; i < count; i++)
            {
                var at = 19 + i * 12;
                var kind = packet[at + 3]; var length = U16(packet, at + 4); var db = U16(packet, at + 6);
                var addr = packet[at + 9] * 65536 + packet[at + 10] * 256 + packet[at + 11];
                var offset = addr / 8; var bit = addr % 8;
                var valid = packet[at] == 0x12 && packet[at + 1] == 10 && packet[at + 2] == 0x10 && packet[at + 8] == 0x84 &&
                    (kind == 1 && length == 1 || kind == 2 && length > 0 && length <= 960 && bit == 0);
                byte[] raw = [];
                if (function == 5)
                {
                    if (cursor + 4 > packet.Length) throw new InvalidDataException("S7写入头截断");
                    var transport = packet[cursor + 1]; var bits = U16(packet, cursor + 2); var bytes = (bits + 7) / 8;
                    if (cursor + 4 + bytes > packet.Length) throw new InvalidDataException("S7写入数据截断");
                    raw = packet.AsSpan(cursor + 4, bytes).ToArray();
                    valid &= kind == 1 ? transport == 3 && bits == 1 && raw.Length == 1 : transport == 4 && bits == length * 8 && raw.Length == length;
                    cursor += 4 + bytes;
                    if (bytes % 2 != 0 && i < count - 1) cursor++;
                    if (cursor > packet.Length) throw new InvalidDataException("S7写入填充截断");
                }
                items.Add((kind, length, db, offset, bit, valid, raw));
            }
            if (function == 5 && cursor != packet.Length) throw new InvalidDataException("S7写入含额外数据");
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (function == 4)
                {
                    var raw = item.Valid ? _lab.Read(item.Db, item.Offset, item.Count) : null;
                    if (raw is null) { output.Write([0x05, 0, 0, 0]); continue; }
                    if (item.Kind == 1) raw = [(byte)((raw[0] >> item.Bit) & 1)];
                    output.Write([0xff, (byte)(item.Kind == 1 ? 3 : 4)]);
                    var bitLength = new byte[2]; Put16(bitLength, 0, item.Kind == 1 ? 1 : raw.Length * 8);
                    output.Write(bitLength); output.Write(raw);
                    if (raw.Length % 2 != 0 && i < items.Count - 1) output.WriteByte(0);
                }
                else output.WriteByte(item.Valid && _lab.WriteHost(item.Db, item.Offset, item.Bit, item.Raw, item.Kind == 1, peer) ? (byte)0xff : (byte)0x03);
            }
        }
        else throw new InvalidDataException($"未实现S7功能{function}（仅支持协商、DB BOOL/BYTE读写）");
        var data = output.ToArray();
        if (parameters.Length + data.Length > 65000) throw new InvalidDataException("响应超长");
        var reply = new byte[19 + parameters.Length + data.Length];
        reply[0] = 3; Put16(reply, 2, reply.Length); reply[4] = 2; reply[5] = 0xf0; reply[6] = 0x80;
        reply[7] = 0x32; reply[8] = 3; reply[11] = packet[11]; reply[12] = packet[12];
        Put16(reply, 13, parameters.Length); Put16(reply, 15, data.Length);
        parameters.CopyTo(reply, 19); data.CopyTo(reply, 19 + parameters.Length);
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        if (_accept is not null) await _accept.ConfigureAwait(false);
        var tasks = _clients.Values.ToArray();
        foreach (var client in _clients.Keys) client.Dispose();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _stop.Dispose();
    }
}
