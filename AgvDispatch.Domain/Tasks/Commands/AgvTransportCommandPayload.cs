using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Domain.Tasks;

public sealed record AgvTransportCommandPayload(
    string BusinessTaskId,
    string VehicleId,
    string Source,
    string Destination,
    IReadOnlyList<string> Path,
    AgvTransportAction? Action);
