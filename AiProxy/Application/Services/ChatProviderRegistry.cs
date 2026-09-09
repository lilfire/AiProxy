using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed class ChatProviderRegistry : IChatProviderRegistry
{
    private readonly IEnumerable<IChatProvider> _providers;
    private readonly IModelIdCache _modelIdCache;
    private readonly ILogger<ChatProviderRegistry> _logger;
    private readonly IRuntimeSettings _settings;

    public ChatProviderRegistry(
        IEnumerable<IChatProvider> providers,
        IModelIdCache modelIdCache,
        IRuntimeSettings settings,
        ILogger<ChatProviderRegistry> logger)
    {
        _providers = providers;
        _modelIdCache = modelIdCache;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ModelResolution> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new UnknownModelException("En modell må angis.");

        var resolution = TryResolveConfiguredAlias(modelId)
            ?? await TryResolveByExplicitPrefixAsync(modelId, cancellationToken)
            ?? await TryResolveByKnownModelAsync(modelId, cancellationToken)
            ?? TryResolveByBuiltInPrefix(modelId);

        if (resolution != null)
            return resolution;

        _logger.LogWarning("Modell {Model} ble ikke funnet hos noen aktiv provider", modelId);
        throw new UnknownModelException($"Modellen '{modelId}' ble ikke funnet.");
    }

    private async Task<ModelResolution?> TryResolveByExplicitPrefixAsync(string modelId, CancellationToken cancellationToken)
    {
        var prefixResult = SplitProviderPrefix(modelId);

        if (string.IsNullOrEmpty(prefixResult.ProviderName))
            return null;

        var targetProvider = FindProviderByName(prefixResult.ProviderName);

        if (targetProvider == null)
            return null;

            _logger.LogInformation("Modell {Model} rutes til {ProviderName} basert på prefiks", modelId, targetProvider.Name);
            return CreateResolution(targetProvider, prefixResult.ModelId, modelId);
    }

    private async Task<ModelResolution?> TryResolveByKnownModelAsync(string modelId, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            if (!IsEnabled(provider.Name))
                continue;

            try
            {
                var availableModelIds = await _modelIdCache.GetOrAddAsync(
                    provider.Name,
                    cancellationToken => provider.GetModelIdsAsync(cancellationToken),
                    cancellationToken);

                var matchingModelId = availableModelIds
                    .FirstOrDefault(availableModelId => availableModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));

                if (matchingModelId == null)
                    continue;

                _logger.LogInformation("Modell {Model} rutes til {ProviderName}", modelId, provider.Name);
                return CreateResolution(provider, matchingModelId, modelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Klarte ikke å hente modelliste fra {ProviderName}", provider.Name);
            }
        }

        return null;
    }

    private ModelResolution? TryResolveByBuiltInPrefix(string modelId)
    {
        if (modelId.StartsWith(OpenAiConstants.ModelPrefixes.Grok, StringComparison.OrdinalIgnoreCase))
        {
            var grokProvider = FindProviderByName(OpenAiConstants.Providers.Grok);

            if (grokProvider != null)
            {
                _logger.LogInformation("Modell {Model} rutes til {ProviderName} basert på prefiks", modelId, grokProvider.Name);
                return CreateResolution(grokProvider, modelId, modelId);
            }
        }

        if (modelId.StartsWith(OpenAiConstants.ModelPrefixes.Claude, StringComparison.OrdinalIgnoreCase))
        {
            var claudeProvider = FindProviderByName(OpenAiConstants.Providers.Claude);

            if (claudeProvider != null)
            {
                _logger.LogInformation("Modell {Model} rutes til {ProviderName} basert på prefiks", modelId, claudeProvider.Name);
                return CreateResolution(claudeProvider, modelId, modelId);
            }
        }

        if (modelId.StartsWith(OpenAiConstants.ModelPrefixes.Codex, StringComparison.OrdinalIgnoreCase))
        {
            var codexProvider = FindProviderByName(OpenAiConstants.Providers.Codex);

            if (codexProvider != null)
            {
                _logger.LogInformation("Modell {Model} rutes til {ProviderName} basert på prefiks", modelId, codexProvider.Name);
                return CreateResolution(codexProvider, modelId, modelId);
            }
        }

        if (modelId.StartsWith(OpenAiConstants.ModelPrefixes.M365Copilot, StringComparison.OrdinalIgnoreCase))
        {
            var m365Provider = FindProviderByName(OpenAiConstants.Providers.M365Copilot);

            if (m365Provider != null)
            {
                _logger.LogInformation("Modell {Model} rutes til {ProviderName} basert på prefiks", modelId, m365Provider.Name);
                return CreateResolution(m365Provider, modelId, modelId);
            }
        }

        return null;
    }

    private IChatProvider? FindProviderByName(string providerName)
    {
        return _providers
            .FirstOrDefault(p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase) && IsEnabled(p.Name));
    }

    public async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var allModels = new List<ModelConfig>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var providerTasks = _providers.Where(provider => IsEnabled(provider.Name)).Select(provider => Task.Run(async () =>
        {
            try
            {
                var modelIds = await _modelIdCache.GetOrAddAsync(
                    provider.Name,
                    providerToken => provider.GetModelIdsAsync(providerToken),
                    cancellationToken);

                return (ProviderName: provider.Name, ModelIds: modelIds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Klarte ikke å hente modeller fra {ProviderName}", provider.Name);
                return (ProviderName: provider.Name, ModelIds: Array.Empty<string>() as IReadOnlyList<string>);
            }
        }, cancellationToken));

        var results = await Task.WhenAll(providerTasks);

        foreach (var (providerName, modelIds) in results)
        {
            foreach (var modelId in modelIds)
            {
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                var setting = FindModelSetting(providerName, modelId);
                if (setting is { Enabled: false })
                    continue;

                var id = string.IsNullOrWhiteSpace(setting?.Alias) ? $"{providerName}-{modelId}" : setting.Alias.Trim();

                if (!seenIds.Add(id))
                {
                    _logger.LogWarning("Duplikat modell-ID {ModelId} fra {ProviderName} ignoreres", id, providerName);
                    continue;
                }

                allModels.Add(new ModelConfig(id, modelId, providerName));
            }
        }

        return allModels;
    }

    private ProviderPrefixResult SplitProviderPrefix(string modelId)
    {
        var separatorIndex = modelId.IndexOf('-');

        if (separatorIndex <= 0)
            return new ProviderPrefixResult(null, modelId);

        var prefix = modelId[..separatorIndex];
        var rest = modelId[(separatorIndex + 1)..];

        return new ProviderPrefixResult(prefix, rest);
    }

    private ModelResolution CreateResolution(IChatProvider provider, string modelId, string displayModelId)
    {
        return new ModelResolution
        {
            Provider = provider,
            ModelId = modelId,
            DisplayModelId = displayModelId
        };
    }

    private bool IsEnabled(string providerName) =>
        !_settings.Current.Providers.TryGetValue(providerName, out var enabled) || enabled;

    private AdminModelSetting? FindModelSetting(string providerName, string modelId) =>
        _settings.Current.Models.FirstOrDefault(setting =>
            setting.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
            setting.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    private ModelResolution? TryResolveConfiguredAlias(string modelId)
    {
        var setting = _settings.Current.Models.FirstOrDefault(item => item.Enabled &&
            !string.IsNullOrWhiteSpace(item.Alias) && item.Alias.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (setting == null)
            return null;

        var provider = FindProviderByName(setting.ProviderName);
        return provider == null ? null : CreateResolution(provider, setting.ModelId, setting.Alias);
    }
}
