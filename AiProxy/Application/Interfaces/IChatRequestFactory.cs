using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Interfaces;

public interface IChatRequestFactory
{
    OpenAiChatRequest CreateProviderRequest(OpenAiChatRequest request, ModelResolution resolution);
}
