namespace AiProxy.Services.Logging;

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception);
