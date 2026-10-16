namespace AgvDispatch.Domain.Tasks;

public sealed record TaskCreationResult(
    bool Created,
    ActiveDispatchTask Task,
    string? Reason = null);
