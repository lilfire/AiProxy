using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

public interface IModelService
{
    Task<IReadOnlyList<OpenAiModel>> GetModelsAsync(CancellationToken cancellationToken = default);
}
