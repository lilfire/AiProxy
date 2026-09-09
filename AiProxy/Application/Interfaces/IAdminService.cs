using AiProxy.Configuration;

namespace AiProxy.Application.Interfaces;

public interface IAdminService
{
    AdminSettings GetSettings();
    Task SaveSettingsAsync(AdminSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminDiscoveredModel>> GetDiscoveredModelsAsync(CancellationToken cancellationToken = default);
    Task RefreshModelsAsync(CancellationToken cancellationToken = default);
}

public sealed record AdminProviderStatus(string Name, bool Enabled, bool Available, string Detail, ProviderUsageSnapshot Usage, ProviderQuotaSnapshot? Quota);
public sealed record AdminDiscoveredModel(string ProviderName, string ModelId);
