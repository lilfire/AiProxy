using System.Collections.Concurrent;

namespace AiProxy.Services;

public sealed class ProviderSessionStore
{
    private readonly ConcurrentDictionary<ProviderSessionKey, string> _providerSessionIds = new();

    public ProviderSessionResult GetOrCreateSessionId(string sessionId, string providerName, Func<string> createId)
    {
        var key = new ProviderSessionKey(sessionId, providerName);

        if (_providerSessionIds.TryGetValue(key, out var existingId))
            return new ProviderSessionResult(existingId, WasCreated: false);

        var newId = createId();
        var addedId = _providerSessionIds.GetOrAdd(key, newId);
        var wasCreated = ReferenceEquals(addedId, newId);

        return new ProviderSessionResult(addedId, wasCreated);
    }

    public bool HasSession(string sessionId, string providerName)
    {
        return _providerSessionIds.ContainsKey(new ProviderSessionKey(sessionId, providerName));
    }

    public void Reset(string sessionId, string providerName)
    {
        _providerSessionIds.TryRemove(new ProviderSessionKey(sessionId, providerName), out _);
    }
}
