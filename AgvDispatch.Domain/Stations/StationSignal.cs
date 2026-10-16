namespace AgvDispatch.Domain.Stations;

public sealed record StationSignal(
    string StationId,
    bool FullBoxRequested,
    bool BoxPresent,
    bool Faulted,
    bool CommunicationOnline,
    DateTimeOffset UpdatedAt);
