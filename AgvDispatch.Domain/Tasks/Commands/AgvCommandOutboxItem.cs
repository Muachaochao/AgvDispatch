namespace AgvDispatch.Domain.Tasks;

public sealed record AgvCommandOutboxItem(
    string CommandId,
    string IdempotencyKey,
    string TaskId,
    string StepId,
    string VehicleId,
    AgvCommandType CommandType,
    AgvCommandStatus Status,
    string PayloadJson,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? SentAt,
    string? VendorCommandId,
    string? LastError);
