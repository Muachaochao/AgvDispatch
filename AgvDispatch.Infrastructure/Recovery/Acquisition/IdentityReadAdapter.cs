using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
namespace AgvDispatch.Infrastructure.Persistence;

internal sealed record IdentityReadSample(PlcExecutionIdentity? Plc, VehicleContainerIdentity? Vehicle, bool Valid, string Message);
internal abstract class IdentityReadAdapter(string kind, string id)
{
    public string Kind { get; } = kind;
    public string Id { get; } = id;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    public async Task<IdentityReadSample> ReadAsync(CancellationToken ct)
    {
        await _readGate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { _readGate.Release(); }
    }
    protected abstract Task<IdentityReadSample> ReadCoreAsync(CancellationToken ct);
    protected static bool Present(string? text, int max = 128) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;
}
internal sealed class PlcIdentityReadAdapter(IPlcExecutionIdentityReader reader, TimeProvider clock, int attempts)
    : IdentityReadAdapter("PLC", reader.StationId)
{
    protected override async Task<IdentityReadSample> ReadCoreAsync(CancellationToken ct)
    {
        PlcIdentityFields? fields = null; PlcIdentityStamp? stamp = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var before = await reader.ReadStampAsync(ct); ct.ThrowIfCancellationRequested();
            fields = await reader.ReadFieldsAsync(ct); ct.ThrowIfCancellationRequested();
            var after = await reader.ReadStampAsync(ct);
            ct.ThrowIfCancellationRequested(); stamp = after;
            var coherent = before == after && after.IsStable && after.Quality == SignalQuality.Good &&
                Present(after.DeviceSessionId, 64) && Present(after.CycleId, 64) && Present(after.SnapshotVersion, 64);
            if (!coherent) continue;
            var valid = fields.SchemaVersion == 1 && fields.StationId == Id && fields.Quality == SignalQuality.Good &&
                Present(fields.TaskId, 64) && Present(fields.ContainerId) && Present(fields.OriginalBusinessStepId, 64) &&
                (fields.OriginalVendorTaskId == null || Present(fields.OriginalVendorTaskId));
            return new(new(Id, fields.TaskId, after.DeviceSessionId, after.CycleId, fields.ContainerId,
                fields.OriginalBusinessStepId, fields.OriginalVendorTaskId, valid ? SignalQuality.Good : SignalQuality.Bad,
                clock.GetUtcNow(), coherent, fields.SchemaVersion), null, valid,
                valid ? "PLC身份一致读取完成。" : "PLC身份字段、归属、版本或质量无效。");
        }
        // No coherent capture: retain what was reported, explicitly invalid. It cannot form a valid intent baseline.
        return new(new(Id, fields?.TaskId ?? "", stamp?.DeviceSessionId ?? "", stamp?.CycleId ?? "", fields?.ContainerId ?? "",
            fields?.OriginalBusinessStepId ?? "", fields?.OriginalVendorTaskId, SignalQuality.Bad, clock.GetUtcNow(), false,
            fields?.SchemaVersion ?? 1), null, false, "PLC身份读取期间周期/版本变化或更新未结束，已达到一致读取次数限制。");
    }
}
internal sealed class VehicleIdentityReadAdapter(IVehicleContainerIdentityReader reader, TimeProvider clock)
    : IdentityReadAdapter("AGV", reader.VehicleId)
{
    protected override async Task<IdentityReadSample> ReadCoreAsync(CancellationToken ct)
    {
        var frame = await reader.ReadAtomicAsync(ct); ct.ThrowIfCancellationRequested();
        var valid = frame.SchemaVersion == 1 && frame.VehicleId == Id && frame.IsAtomic && frame.Quality == SignalQuality.Good &&
            Enum.IsDefined(frame.LoadState) && (frame.LoadState != ContainerLoadState.Empty || string.IsNullOrEmpty(frame.ContainerId)) &&
            (frame.LoadState != ContainerLoadState.Loaded || Present(frame.ContainerId) && Present(frame.OriginalBusinessStepId, 64)) &&
            (frame.OriginalVendorTaskId == null || Present(frame.OriginalVendorTaskId));
        return new(null, new(Id, frame.LoadState, frame.ContainerId, frame.OriginalBusinessStepId, frame.OriginalVendorTaskId,
            valid ? SignalQuality.Good : SignalQuality.Bad, clock.GetUtcNow(), frame.IsAtomic, frame.SchemaVersion), valid,
            valid ? "车辆容器快照读取完成；未知状态仍需现场核对。" : "车辆容器快照不是完整一致读取，或字段/归属无效。");
    }
}
