using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

public interface IResponsesService
{
    Task<IResult> ExecuteAsync(OpenAiResponsesRequest request, string sessionId, CancellationToken cancellationToken = default);
}
