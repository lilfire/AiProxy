using System.Collections.Concurrent;
using AiProxy.Application.Interfaces;

namespace AiProxy.Services;

/// <summary>
/// In-memory request usage for the local proxy. Provider CLIs do not expose a
/// common, reliable token or subscription-quota API, so those values are not inferred here.
/// </summary>
public sealed class ProviderUsageStore : IProviderUsageStore
{
    private readonly ConcurrentDictionary<string, ProviderUsageCounter> _counters = new(StringComparer.OrdinalIgnoreCase);

    public void RecordCompletedRequest(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return;

        _counters.GetOrAdd(providerName, _ => new ProviderUsageCounter()).Record(DateTimeOffset.UtcNow);
    }

    public ProviderUsageSnapshot GetSnapshot(string providerName) =>
        _counters.TryGetValue(providerName, out var counter)
            ? counter.GetSnapshot(DateTimeOffset.UtcNow)
            : new ProviderUsageSnapshot(0, 0, null);

    private sealed class ProviderUsageCounter
    {
        private readonly object _sync = new();
        private readonly Queue<DateTimeOffset> _completedLast24Hours = new();
        private int _completedRequests;
        private DateTimeOffset? _lastUsedAt;

        public void Record(DateTimeOffset timestamp)
        {
            lock (_sync)
            {
                _completedRequests++;
                _lastUsedAt = timestamp;
                _completedLast24Hours.Enqueue(timestamp);
                RemoveExpired(timestamp);
            }
        }

        public ProviderUsageSnapshot GetSnapshot(DateTimeOffset now)
        {
            lock (_sync)
            {
                RemoveExpired(now);
                return new ProviderUsageSnapshot(_completedRequests, _completedLast24Hours.Count, _lastUsedAt);
            }
        }

        private void RemoveExpired(DateTimeOffset now)
        {
            var threshold = now.AddHours(-24);
            while (_completedLast24Hours.TryPeek(out var timestamp) && timestamp < threshold)
                _completedLast24Hours.Dequeue();
        }
    }
}
