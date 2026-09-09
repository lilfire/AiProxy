using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

public interface IChatCompletionService
{
    Task<IResult> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default);
}
