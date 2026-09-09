namespace AiProxy.Services.Sessions;

public sealed record SessionHistoryMessage(string Role, string Content);

public sealed record SessionHistoryTurn(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Api,
    string Model,
    string? Provider,
    string Status,
    IReadOnlyList<SessionHistoryMessage> Input,
    string? Output,
    string? Error);

public sealed record SessionHistorySession(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<SessionHistoryTurn> Turns);

public sealed record SessionHistoryChanged(string SessionId, DateTimeOffset Timestamp);
