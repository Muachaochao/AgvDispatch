// ManualTaskCreationService requires Guid.TryParseExact(requestId, "N", ...).
// Persist this value with its original payload; a retry must not generate a second ID.
export const newTaskRequestId = (): string => crypto.randomUUID().replaceAll('-', '');
