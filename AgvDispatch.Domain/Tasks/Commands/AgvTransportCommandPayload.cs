using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Domain.Tasks;

public sealed record AgvTransportCommandPayload(
    string BusinessTaskId,
    string VehicleId,
    string Source,
    string Destination,
    IReadOnlyList<string> Path,
    AgvTransportAction? Action)
{
    // Window zero keeps the legacy command shape. Rolling-route windows use the
    // stable business step id below for all audit and recovery identity checks.
    public string BusinessStepId { get; init; } = "";
    public int WindowIndex { get; init; }
    public int WindowCount { get; init; } = 1;
    public string? PreviousVendorTaskId { get; init; }
    public AgvTransportAction? FinalAction { get; init; }
    public IReadOnlyList<AgvTransportRouteWindow> Windows { get; init; } = [];
}

public sealed record AgvTransportRouteWindow(
    int Index,
    string Source,
    string Destination,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<string> ResourceIds,
    bool Final);
