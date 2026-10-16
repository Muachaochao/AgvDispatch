namespace AgvDispatch.Domain.Agvs;

public sealed record AgvTransportAction(
    string Operation,
    double? JackHeight = null,
    bool? Recognize = null,
    bool? UsePgv = null,
    bool? UseDownPgv = null);
