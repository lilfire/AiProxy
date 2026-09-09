using System.Collections.Concurrent;
using AiProxy.Application.Interfaces;
using AiProxy.Configuration;

namespace AiProxy.Application.Services;

public sealed class ModelIdCache : IModelIdCache
{
    private readonly ConcurrentDictionary<string, ModelIdCacheEntry> _entries = new();
    private readonly IRuntimeSettings _settings;

    public ModelIdCache(IRuntimeSettings settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<string>> GetOrAddAsync(
        string providerName,
        Func<CancellationToken, Task<IReadOnlyList<string>>> factory,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (_entries.TryGetValue(providerName, out var existing) && existing.ExpiresAt > now)
            return existing.ModelIds;

        var modelIds = await factory(cancellationToken);
        var entry = new ModelIdCacheEntry(modelIds, now.AddSeconds(_settings.Current.CacheTtlSeconds));
        _entries[providerName] = entry;

        return modelIds;
    }

    public void Clear(string? providerName = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            _entries.Clear();
            return;
        }

        _entries.TryRemove(providerName, out _);
    }
}
