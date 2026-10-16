using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
namespace AgvDispatch.Infrastructure.Persistence;

// No physical protocol or field addresses are chosen here. Real readers must be registered after site validation.
public sealed class ExecutionIdentityAcquisition
{
    private readonly ExecutionIdentityAcquisitionOptions _options;
    private readonly ExecutionIdentityCache _cache;
    private readonly IdentityReadAdapter[] _adapters;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _publicationGate = new();
    private bool _active;
    private int _generation;
    public ExecutionIdentityAcquisition(ExecutionIdentityAcquisitionOptions options, ExecutionIdentityCache cache,
        IEnumerable<IPlcExecutionIdentityReader> plcs, IEnumerable<IVehicleContainerIdentityReader> vehicles, TimeProvider? clock = null)
    {
        _options = options; _cache = cache;
        var time = clock ?? TimeProvider.System;
        _adapters = plcs.Select(p => (IdentityReadAdapter)new PlcIdentityReadAdapter(p, time, options.CoherentReadAttempts))
            .Concat(vehicles.Select(v => (IdentityReadAdapter)new VehicleIdentityReadAdapter(v, time))).ToArray();
        if (options.PollIntervalMs is < 100 or > 60000 || options.ReadTimeoutMs is < 50 or > 60000 || options.CoherentReadAttempts is < 1 or > 5)
            throw new ArgumentException("身份采集轮询/超时/一致读取次数配置无效。");
        if (_adapters.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 128) ||
            _adapters.GroupBy(a => (a.Kind, a.Id)).Any(g => g.Count() > 1))
            throw new ArgumentException("身份采集源编号为空、过长或重复。");
        if (options.Enabled && _adapters.Length == 0)
            throw new InvalidOperationException("身份采集已启用，但没有注册经现场确认的只读采集源。");
    }
    public int SourceCount => _adapters.Length;
    public async Task<IReadOnlyList<IdentityAcquisitionResult>> PollOnceAsync(CancellationToken ct)
    {
        if (!_options.Enabled) { MarkStopped(); return []; }
        await _pollGate.WaitAsync(ct);
        try
        {
            if (!_options.Enabled) { MarkStopped(); return []; }
            int generation;
            lock (_publicationGate) { _active = true; generation = _generation; }
            return await Task.WhenAll(_adapters.Select(a => PollAsync(a, generation, ct)));
        }
        finally { _pollGate.Release(); }
    }
    private async Task<IdentityAcquisitionResult> PollAsync(IdentityReadAdapter adapter, int generation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ReadTimeoutMs);
        try
        {
            var read = adapter.ReadAsync(timeout.Token);
            // Observe eventual errors from a driver that ignores cancellation. Per-adapter gate prevents overlapping I/O.
            _ = read.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var sample = await read.WaitAsync(timeout.Token);
            lock (_publicationGate)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (generation != _generation) return new(adapter.Kind, adapter.Id, "Stopped", "采集已停止，不发布迟到样本。");
                if (!_options.Enabled) { Invalidate(adapter, SignalQuality.Disabled); return new(adapter.Kind, adapter.Id, "Stopped", "采集已关闭，不发布新样本。"); }
                if (sample.Plc != null) _cache.Publish(sample.Plc);
                if (sample.Vehicle != null) _cache.Publish(sample.Vehicle);
            }
            return new(adapter.Kind, adapter.Id, sample.Valid ? "Good" : "Invalid", sample.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Invalidate(adapter, SignalQuality.Disabled, generation); throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Invalidate(adapter, SignalQuality.Bad, generation); return new(adapter.Kind, adapter.Id, "Timeout", "身份读取超时，旧样本已失效。"); }
        catch (Exception) { Invalidate(adapter, SignalQuality.Bad, generation); return new(adapter.Kind, adapter.Id, "Failed", "身份读取失败，旧样本已失效。"); }
    }
    public void MarkStopped()
    {
        lock (_publicationGate)
        {
            if (!_active) return;
            _generation++; _active = false;
            foreach (var adapter in _adapters) Invalidate(adapter, SignalQuality.Disabled);
        }
    }
    private void Invalidate(IdentityReadAdapter adapter, SignalQuality quality, int? generation = null)
    {
        lock (_publicationGate)
        {
            if (generation != null && generation != _generation) return;
            InvalidateCore(adapter, _options.Enabled ? quality : SignalQuality.Disabled);
        }
    }
    private void InvalidateCore(IdentityReadAdapter adapter, SignalQuality quality)
    {
        // Retain last successful read time and identifiers for diagnostics, never freshen a failed read.
        if (adapter.Kind == "PLC")
        {
            var old = _cache.TryGetPlc(adapter.Id, out var sample) ? sample :
                new PlcExecutionIdentity(adapter.Id, "", "", "", "", "", null, SignalQuality.Unknown, DateTimeOffset.MinValue, false);
            _cache.Publish(old with { Quality = quality, IsCoherent = false });
        }
        else
        {
            var old = _cache.TryGetVehicle(adapter.Id, out var sample) ? sample :
                new VehicleContainerIdentity(adapter.Id, ContainerLoadState.Unknown, null, null, null, SignalQuality.Unknown, DateTimeOffset.MinValue, false);
            _cache.Publish(old with { Quality = quality, IsCoherent = false });
        }
    }
}
