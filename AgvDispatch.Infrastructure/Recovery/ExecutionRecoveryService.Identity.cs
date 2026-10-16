using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class ExecutionRecoveryService
{
    private static RecoveryIdentityVerification StoredIdentity(string evidence)
    {
        try
        {
            using var json = JsonDocument.Parse(evidence);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Identity", out var data))
                return data.Deserialize<RecoveryIdentityVerification>() ?? new("Unavailable", "身份字段尚未采集。", null);
        }
        catch (JsonException) { }
        return new("Unavailable", "无法自动核验：设备身份字段尚未接入，保留现场核对。", null);
    }
    private async Task<RecoveryIdentityVerification> ObserveIdentityAsync(DispatchTaskEntity? task, DispatchTaskStepEntity? step,
        AgvCommandOutboxEntity? command, bool unstarted, CancellationToken ct)
    {
        if (unstarted) return new("NotRequired", "未开始请求无执行意图，本条不使用身份字段证明动作完成。", null);
        if (task == null || step == null)
        {
            var samples = identities?.GetPlc() ?? [];
            var loads = identities?.GetVehicles() ?? [];
            var bad = samples.Any(p => p.SchemaVersion != 1 || string.IsNullOrWhiteSpace(p.TaskId) ||
                string.IsNullOrWhiteSpace(p.DeviceSessionId) || p.DeviceSessionId.Length > 64 ||
                string.IsNullOrWhiteSpace(p.CycleId) || p.CycleId.Length > 64 ||
                string.IsNullOrWhiteSpace(p.ContainerId) || p.ContainerId.Length > 128 ||
                string.IsNullOrWhiteSpace(p.OriginalBusinessStepId) || p.OriginalBusinessStepId.Length > 64 || p.Quality != SignalQuality.Good || !p.IsCoherent || p.ObservedAt < _started ||
                p.ObservedAt > DateTimeOffset.Now.AddSeconds(1) || DateTimeOffset.Now - p.ObservedAt > settings.StateMaxAge) ||
                loads.Any(v => v.SchemaVersion != 1 || !Enum.IsDefined(v.LoadState) ||
                v.LoadState == ContainerLoadState.Empty && !string.IsNullOrEmpty(v.ContainerId) ||
                v.LoadState == ContainerLoadState.Loaded && string.IsNullOrWhiteSpace(v.ContainerId) || v.Quality != SignalQuality.Good || !v.IsCoherent || v.ObservedAt < _started ||
                v.ObservedAt > DateTimeOffset.Now.AddSeconds(1) || DateTimeOffset.Now - v.ObservedAt > settings.StateMaxAge);
            return new(bad ? "Blocked" : "Unavailable", "全场身份核对：须逐任务关联PLC周期与容器；全场物理状态仍需现场确认。",
                bad ? "全场已接入的身份样本质量、一致性或新鲜时间不合格。" :
                identityOptions?.RequireMachineIdentity == true && (samples.Count == 0 || loads.Count == 0) ? "身份核验要求已启用，但全场身份字段未接入。" : null);
        }
        var transport = task.CancelStepId == null ? null : await database.Queryable<DispatchTaskStepEntity>().Where(s => s.StepId == task.CancelStepId && s.TaskId == task.TaskId).FirstAsync(ct);
        transport ??= step.StepType is 10 or 30 or 50 or 70 ? step :
            await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId && s.Sequence < step.Sequence &&
                (s.StepType == 10 || s.StepType == 30 || s.StepType == 50 || s.StepType == 70)).OrderByDescending(s => s.Sequence).FirstAsync(ct);
        var business = transport?.StepId ?? step.StepId;
        var vendor = task.CancelVendorId ?? transport?.VendorTaskId ?? (command?.StepId == business ? command.VendorCommandId : null);
        var vehicleId = task.CancelVehicleId ?? step.VehicleId ?? task.VehicleId;
        var station = PlcStation(task, step);
        PlcExecutionIdentity? plc = null; VehicleContainerIdentity? load = null;
        if (identities != null) { identities.TryGetPlc(station, out plc!); identities.TryGetVehicle(vehicleId ?? "", out load!); }
        // SQLite append order identifies the first intent even when wall-clock timestamps tie.
        // First intent wins, including a missing baseline. Never reinterpret a later retry as original physical identity.
        var intent = await database.Queryable<ExecutionEvidenceEntity>().Where(e => e.TaskId == task.TaskId && e.StepId == step.StepId &&
            (e.Kind == "PlcWriteIntent" || e.Kind == "AgvSendIntent")).OrderBy("rowid ASC").FirstAsync(ct);
        if (intent == null && business != step.StepId)
            intent = await database.Queryable<ExecutionEvidenceEntity>().Where(e => e.TaskId == task.TaskId && e.StepId == business && e.Kind == "AgvSendIntent")
                .OrderBy("rowid ASC").FirstAsync(ct);
        ExecutionIdentityBaseline? baseline = null;
        if (intent != null)
        {
            try
            {
                using var json = JsonDocument.Parse(intent.Evidence);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("ExecutionIdentityBaseline", out var data)) baseline = data.Deserialize<ExecutionIdentityBaseline>();
            }
            catch (JsonException) { }
        }
        return ExecutionIdentityVerifier.Verify(task.TaskId, station, business, vendor, vehicleId, plc, load, baseline,
            _started, DateTimeOffset.Now, settings.StateMaxAge, identityOptions?.RequireMachineIdentity == true);
    }
    private object IdentityFingerprint(Observation observation)
    {
        object Plc(PlcExecutionIdentity p) => new { p.SchemaVersion, p.StationId, p.TaskId, p.DeviceSessionId, p.CycleId, p.ContainerId,
            p.OriginalBusinessStepId, p.OriginalVendorTaskId, p.Quality, p.IsCoherent };
        object Vehicle(VehicleContainerIdentity v) => new { v.SchemaVersion, v.VehicleId, v.LoadState, v.ContainerId,
            v.OriginalBusinessStepId, v.OriginalVendorTaskId, v.Quality, v.IsCoherent };
        return observation.Task == null ? new { Plc = (identities?.GetPlc() ?? []).Select(Plc).ToArray(), Vehicles = (identities?.GetVehicles() ?? []).Select(Vehicle).ToArray() } :
            (object)new { Plc = observation.Identity.Plc == null ? null : Plc(observation.Identity.Plc),
                Vehicle = observation.Identity.Vehicle == null ? null : Vehicle(observation.Identity.Vehicle) };
    }
}
