using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Interfaces;

public interface IChatProviderRegistry
{
    Task<ModelResolution> ResolveAsync(string modelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken = default);
}
