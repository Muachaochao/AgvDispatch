using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

/// <summary>Layer five returns a rejection reason or null; it grants no permission and sends no command.</summary>
public interface IDispatchPreSendSafetyService
{
    Task<string?> CheckPreparationAsync(DispatchTaskStepWorkItem step, string vehicleId,
        IReadOnlyList<string> path, ActiveMapRuntimeSnapshot? expectedMap, CancellationToken ct);
    Task<string?> CheckPendingCommandAsync(AgvCommandOutboxItem command, CancellationToken ct);
}
