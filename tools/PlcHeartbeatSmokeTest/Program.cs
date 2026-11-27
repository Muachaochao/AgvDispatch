using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var catalog = PlcSignalCatalog.FromSignals(
[
    new("DB1.DBX0.0", "Pulse", "心跳", "HB", "Test", 1, "BOOL", "PLC->Host", "Read", "Confirmed",
        true, "", "", "test"),
    new("DB1.DBX0.1", "Ready", "就绪", "STATION", "Test", 1, "BOOL", "PLC->Host", "Read", "Confirmed",
        true, "", "", "test")
]);
var options = new PlcOptions
{
    Enabled = true,
    PollIntervalMs = 20,
    StaleAfterMs = 200,
    InterruptedFailureThreshold = 2,
    HeartbeatStationId = "HB",
    HeartbeatVariableName = "Pulse",
    HeartbeatTimeoutMs = 120
};
var cache = new PlcSignalCache(catalog);
var supervisor = new PlcConnectionSupervisor(options);
var reader = new Reader();
var service = new PlcPollingService(
    NullLogger<PlcPollingService>.Instance,
    options,
    reader,
    cache,
    supervisor,
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HB", "STATION" });

using var running = new CancellationTokenSource();
var loop = service.RunAsync(running.Token);
reader.PulseFor(TimeSpan.FromMilliseconds(250));
await Task.Delay(650);
var stalled = cache.GetStation("STATION").Single();
Require(stalled.Quality == SignalQuality.Stale &&
    stalled.ErrorMessage?.Contains("心跳", StringComparison.Ordinal) == true,
    "TCP正常但心跳不变时，业务点位必须标为过期");

reader.PulseFor(TimeSpan.FromMilliseconds(300));
await Task.Delay(300);
var recovered = cache.GetStation("STATION").Single();
Require(recovered.Quality == SignalQuality.Good,
    "心跳恢复变化后，业务点位必须恢复Good");

running.Cancel();
await loop;
Console.WriteLine("PASS: PLC heartbeat gates data freshness independently of TCP.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Reader : AgvDispatch.Application.Abstractions.IPlcBatchReader
{
    private long _pulseUntil;
    private bool _value;

    public void PulseFor(TimeSpan duration) =>
        Interlocked.Exchange(ref _pulseUntil, DateTimeOffset.UtcNow.Add(duration).UtcTicks);

    public Task<IReadOnlyList<PlcSignalReading>> ReadEnabledSignalsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _pulseUntil))
            _value = !_value;
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<PlcSignalReading> rows =
        [
            new("HB", "Pulse", _value, now),
            new("STATION", "Ready", true, now)
        ];
        return Task.FromResult(rows);
    }
}
