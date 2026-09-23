using System.Collections.Concurrent;
using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class ModelIdCache : IModelIdCache
{
    private readonly ConcurrentDictionary<string, ModelIdCacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
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

        // Aigravity and Grok call this cache again from inside their providers.
        // Only Codex needs single-flight discovery; locking every provider would deadlock them.
        if (!providerName.Equals(OpenAiConstants.Providers.Codex, StringComparison.OrdinalIgnoreCase))
        {
            var uncachedModelIds = await factory(cancellationToken);
            _entries[providerName] = new ModelIdCacheEntry(uncachedModelIds, DateTimeOffset.UtcNow.AddSeconds(_settings.Current.CacheTtlSeconds));
            return uncachedModelIds;
        }

        var gate = _locks.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_entries.TryGetValue(providerName, out existing) && existing.ExpiresAt > now)
                return existing.ModelIds;

            var modelIds = await factory(cancellationToken);
            _entries[providerName] = new ModelIdCacheEntry(modelIds, DateTimeOffset.UtcNow.AddSeconds(_settings.Current.CacheTtlSeconds));
            return modelIds;
        }
        finally
        {
            gate.Release();
        }
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
