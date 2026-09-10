namespace AiProxy.Application.Interfaces;

/// <summary>Tracks completed requests handled by this AiProxy process.</summary>
public interface IProviderUsageStore
{
    void RecordCompletedRequest(string providerName);
    ProviderUsageSnapshot GetSnapshot(string providerName);
}

public interface IUsageUpdateNotifier
{
    IDisposable Subscribe(Action<string, ProviderUsageSnapshot> listener);
}

public sealed record ProviderUsageSnapshot(int CompletedRequests, int CompletedRequestsLast24Hours, DateTimeOffset? LastUsedAt);

/// <summary>One rolling subscription-quota window reported by a provider CLI.</summary>
public sealed record ProviderQuotaWindow(string Name, int UsedPercent, DateTimeOffset? ResetsAt, int? DurationMinutes);

/// <summary>
/// The authenticated provider-account quota. This is deliberately separate from
/// <see cref="ProviderUsageSnapshot"/>, which only counts requests made through AiProxy.
/// </summary>
public sealed record ProviderQuotaSnapshot(IReadOnlyList<ProviderQuotaWindow> Windows, DateTimeOffset RetrievedAt, string? Error);

public interface IProviderQuotaService
{
    /// <summary>Returns the last background-refreshed value without starting an external command.</summary>
    ProviderQuotaSnapshot? GetCachedSnapshot(string providerName);
    Task<ProviderQuotaSnapshot?> GetSnapshotAsync(string providerName, CancellationToken cancellationToken = default);
}

public interface IQuotaUpdateNotifier
{
    IDisposable Subscribe(Action<string, ProviderQuotaSnapshot> listener);
}
