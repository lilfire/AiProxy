namespace AiProxy.Application.Services;

internal sealed class ModelIdCacheEntry
{
    public ModelIdCacheEntry(IReadOnlyList<string> modelIds, DateTimeOffset expiresAt)
    {
        ModelIds = modelIds;
        ExpiresAt = expiresAt;
    }

    public IReadOnlyList<string> ModelIds { get; }
    public DateTimeOffset ExpiresAt { get; }
}