using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed class AdminService : IAdminService
{
    private static readonly IReadOnlyDictionary<string, string?> Commands = new Dictionary<string, string?>
    {
        [OpenAiConstants.Providers.Aigravity] = "agy",
        [OpenAiConstants.Providers.Grok] = "grok",
        [OpenAiConstants.Providers.Claude] = "claude",
        [OpenAiConstants.Providers.Codex] = "codex",
        [OpenAiConstants.Providers.M365Copilot] = null
    };

    private readonly IRuntimeSettings _settings;
    private readonly IModelIdCache _modelCache;
    private readonly IExecutablePathResolver _executableResolver;
    private readonly IEnumerable<IChatProvider> _providers;
    private readonly IProviderUsageStore _usageStore;
    private readonly IProviderQuotaService _quotaService;

    public AdminService(
        IRuntimeSettings settings,
        IModelIdCache modelCache,
        IExecutablePathResolver executableResolver,
        IEnumerable<IChatProvider> providers,
        IProviderUsageStore usageStore,
        IProviderQuotaService quotaService)
    {
        _settings = settings;
        _modelCache = modelCache;
        _executableResolver = executableResolver;
        _providers = providers;
        _usageStore = usageStore;
        _quotaService = quotaService;
    }

    public AdminSettings GetSettings() => Clone(_settings.Current);

    public async Task SaveSettingsAsync(AdminSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Providers = Commands.Keys.ToDictionary(
            name => name,
            name => settings.Providers.TryGetValue(name, out var enabled) && enabled,
            StringComparer.OrdinalIgnoreCase);
        settings.M365Copilot.Enabled = settings.Providers[OpenAiConstants.Providers.M365Copilot];
        await _settings.SaveAsync(settings, cancellationToken);
        _modelCache.Clear();
    }

    public Task RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        _modelCache.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminProviderStatus>> GetProviderStatusesAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var statuses = Commands.Select(pair =>
        {
            var enabled = settings.Providers.TryGetValue(pair.Key, out var isEnabled) && isEnabled;
            if (pair.Value == null)
                return new AdminProviderStatus(pair.Key, enabled, settings.M365Copilot.Enabled, enabled ? "M365 Copilot er aktivert." : "M365 Copilot er deaktivert.", _usageStore.GetSnapshot(pair.Key), null);

            var executable = _executableResolver.Resolve(pair.Value);
            var available = File.Exists(executable);
            return new AdminProviderStatus(pair.Key, enabled, available,
                available ? $"Fant {Path.GetFileName(executable)}." : $"Fant ikke '{pair.Value}' på PATH.", _usageStore.GetSnapshot(pair.Key), null);
        }).ToList();

        var statusesWithCachedQuotas = statuses.Select(status => status with
        {
            Quota = _quotaService.GetCachedSnapshot(status.Name)
        }).ToList();
        return Task.FromResult<IReadOnlyList<AdminProviderStatus>>(statusesWithCachedQuotas);
    }

    public async Task<IReadOnlyList<AdminDiscoveredModel>> GetDiscoveredModelsAsync(CancellationToken cancellationToken = default)
    {
        var enabledProviders = _providers.Where(provider =>
            !_settings.Current.Providers.TryGetValue(provider.Name, out var enabled) || enabled);
        var tasks = enabledProviders.Select(async provider =>
        {
            try
            {
                var models = await _modelCache.GetOrAddAsync(provider.Name, provider.GetModelIdsAsync, cancellationToken);
                return models.Select(model => new AdminDiscoveredModel(provider.Name, model));
            }
            catch (Exception)
            {
                return Enumerable.Empty<AdminDiscoveredModel>();
            }
        });
        return (await Task.WhenAll(tasks)).SelectMany(models => models).OrderBy(model => model.ProviderName).ThenBy(model => model.ModelId).ToList();
    }

    private static AdminSettings Clone(AdminSettings settings) => new()
    {
        TimeoutSeconds = settings.TimeoutSeconds,
        CacheTtlSeconds = settings.CacheTtlSeconds,
        TodoBridgeEnabled = settings.TodoBridgeEnabled,
        Providers = new Dictionary<string, bool>(settings.Providers, StringComparer.OrdinalIgnoreCase),
        Models = settings.Models.Select(model => new AdminModelSetting
        {
            ProviderName = model.ProviderName, ModelId = model.ModelId, Alias = model.Alias, Enabled = model.Enabled
        }).ToList(),
        M365Copilot = new M365CopilotOptions
        {
            Enabled = settings.M365Copilot.Enabled,
            EnableInteractiveLogin = settings.M365Copilot.EnableInteractiveLogin,
            CacheDirectory = settings.M365Copilot.CacheDirectory,
            Locale = settings.M365Copilot.Locale,
            TimeZone = settings.M365Copilot.TimeZone,
            WorkspaceDirectory = settings.M365Copilot.WorkspaceDirectory,
            IncludeWorkspaceFiles = settings.M365Copilot.IncludeWorkspaceFiles,
            EnableWorkspaceWrites = settings.M365Copilot.EnableWorkspaceWrites
        }
    };
}
