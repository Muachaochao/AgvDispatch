using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;
namespace AgvDispatch.Service;
public sealed class JournaledPlcGateway(IPlcGateway inner, IExecutionEvidenceJournal journal, IPlcSignalCache signals, IRecoveryBarrier? recovery = null) : IPlcGateway
{
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => inner.GetConnectionStatusAsync(ct);
    public Task<bool?> ReadSignalAsync(string station, string signal, CancellationToken ct) => inner.ReadSignalAsync(station, signal, ct);
    public Task<StationSignal?> ReadStationAsync(string station, CancellationToken ct) => inner.ReadStationAsync(station, ct);
    public async Task WriteSignalAsync(string stationId, string signalName, bool value, CancellationToken ct)
    {
        if (recovery?.IsOpen == false) throw new InvalidOperationException("启动恢复尚未放行，禁止PLC写入。");
        // Commit intent before the external effect; missing acknowledgement stays ambiguous.
        await journal.RecordCurrentAsync("PlcWriteIntent", new { stationId, signalName, value, Signals = signals.GetStation(stationId) }, ct);
        await inner.WriteSignalAsync(stationId, signalName, value, ct);
        await journal.RecordCurrentAsync("PlcWriteAcknowledged", new { stationId, signalName, value }, ct);
    }
}
