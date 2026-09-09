using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

public interface IResponsesRequestMapper
{
    OpenAiChatRequest MapToChatRequest(OpenAiResponsesRequest request);
}
