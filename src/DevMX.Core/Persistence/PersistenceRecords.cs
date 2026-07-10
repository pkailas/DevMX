namespace DevMX.Core.Persistence;

public record StoredMessage(
    long Id,
    long ConversationId,
    int Seq,
    string Role,
    string ContentJson,
    string? Model,
    string CreatedAt);

public record ConversationSummary(
    long Id,
    string Title,
    string Provider,
    string Model,
    string WorkingDir,
    string UpdatedAt);

public record StoredDelegation(
    long Id,
    long ConversationId,
    string JobId,
    string Brief,
    string? FinalState,
    string? JournalJson,
    string CreatedAt);
