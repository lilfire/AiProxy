namespace AiProxy.Services.Logging;

public sealed class InMemoryLogStore : ILogStore
{
    private const int Capacity = 1_000;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new(Capacity);
    private readonly Dictionary<long, Action<LogEntry>> _listeners = [];
    private long _nextListenerId;

    public void Add(LogEntry entry)
    {
        Action<LogEntry>[] listeners;

        lock (_gate)
        {
            if (_entries.Count == Capacity)
                _entries.Dequeue();

            _entries.Enqueue(entry);
            listeners = _listeners.Values.ToArray();
        }

        foreach (var listener in listeners)
            listener(entry);
    }

    public IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    public IDisposable Subscribe(Action<LogEntry> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        long listenerId;
        lock (_gate)
        {
            listenerId = ++_nextListenerId;
            _listeners.Add(listenerId, listener);
        }

        return new Subscription(this, listenerId);
    }

    private void Unsubscribe(long listenerId)
    {
        lock (_gate)
            _listeners.Remove(listenerId);
    }

    private sealed class Subscription(InMemoryLogStore owner, long listenerId) : IDisposable
    {
        private InMemoryLogStore? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(listenerId);
        }
    }
}
