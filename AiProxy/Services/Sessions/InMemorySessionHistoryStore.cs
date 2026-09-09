using AiProxy.Application.Interfaces;

namespace AiProxy.Services.Sessions;

/// <summary>Fanger samtaler kun for den aktive AiProxy-prosessen.</summary>
public sealed class InMemorySessionHistoryStore : ISessionHistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MutableSession> _sessions = [];
    private readonly Dictionary<string, MutableTurn> _turns = [];
    private readonly Dictionary<long, Action<SessionHistoryChanged>> _listeners = [];
    private long _nextListenerId;

    public string StartTurn(string sessionId, string api, string model, IReadOnlyList<SessionHistoryMessage> input)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new MutableTurn(Guid.NewGuid().ToString("N"), sessionId, now, api, model, input.ToArray());
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                _sessions[sessionId] = session = new MutableSession(sessionId, now);
            session.Turns.Add(turn);
            session.LastActivityAt = now;
            _turns.Add(turn.Id, turn);
        }
        Publish(sessionId, now);
        return turn.Id;
    }

    public void SetProvider(string turnId, string provider, string model) => Update(turnId, turn =>
    {
        turn.Provider = provider;
        turn.Model = model;
    });

    public void CompleteTurn(string turnId, string output) => Update(turnId, turn =>
    {
        turn.Output = output;
        turn.Status = "completed";
        turn.CompletedAt = DateTimeOffset.UtcNow;
    });

    public void FailTurn(string turnId, Exception exception) => Update(turnId, turn =>
    {
        turn.Status = "failed";
        turn.Error = exception.GetBaseException().Message;
        turn.CompletedAt = DateTimeOffset.UtcNow;
    });

    public IReadOnlyList<SessionHistorySession> GetSnapshot()
    {
        lock (_gate)
            return _sessions.Values
                .OrderByDescending(session => session.LastActivityAt)
                .Select(ToSnapshot)
                .ToArray();
    }

    public IDisposable Subscribe(Action<SessionHistoryChanged> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        long id;
        lock (_gate)
        {
            id = ++_nextListenerId;
            _listeners.Add(id, listener);
        }
        return new Subscription(this, id);
    }

    private void Update(string turnId, Action<MutableTurn> update)
    {
        string? sessionId = null;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_turns.TryGetValue(turnId, out var turn))
                return;
            update(turn);
            _sessions[turn.SessionId].LastActivityAt = now;
            sessionId = turn.SessionId;
        }
        Publish(sessionId, now);
    }

    private void Publish(string sessionId, DateTimeOffset timestamp)
    {
        Action<SessionHistoryChanged>[] listeners;
        lock (_gate)
            listeners = _listeners.Values.ToArray();
        var change = new SessionHistoryChanged(sessionId, timestamp);
        foreach (var listener in listeners)
            listener(change);
    }

    private static SessionHistorySession ToSnapshot(MutableSession session) => new(
        session.Id, session.StartedAt, session.LastActivityAt,
        session.Turns.Select(turn => new SessionHistoryTurn(turn.Id, turn.StartedAt, turn.CompletedAt, turn.Api,
            turn.Model, turn.Provider, turn.Status, turn.Input, turn.Output, turn.Error)).ToArray());

    private void Unsubscribe(long id)
    {
        lock (_gate)
            _listeners.Remove(id);
    }

    private sealed class MutableSession(string id, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastActivityAt { get; set; } = startedAt;
        public List<MutableTurn> Turns { get; } = [];
    }

    private sealed class MutableTurn(string id, string sessionId, DateTimeOffset startedAt, string api, string model, IReadOnlyList<SessionHistoryMessage> input)
    {
        public string Id { get; } = id;
        public string SessionId { get; } = sessionId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string Api { get; } = api;
        public string Model { get; set; } = model;
        public string? Provider { get; set; }
        public string Status { get; set; } = "in_progress";
        public IReadOnlyList<SessionHistoryMessage> Input { get; } = input;
        public string? Output { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private sealed class Subscription(InMemorySessionHistoryStore owner, long id) : IDisposable
    {
        private InMemorySessionHistoryStore? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}
