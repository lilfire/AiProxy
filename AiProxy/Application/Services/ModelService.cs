using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class ModelService : IModelService
{
    private readonly IChatProviderRegistry _registry;

    public ModelService(IChatProviderRegistry registry)
    {
        _registry = registry;
    }

    public async Task<IReadOnlyList<OpenAiModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var configs = await _registry.GetModelsAsync(cancellationToken);
        return configs.Select(ToOpenAiModel).ToList();
    }

    private OpenAiModel ToOpenAiModel(ModelConfig config)
    {
        return new OpenAiModel(config.Id, config.ProviderName);
    }
}
