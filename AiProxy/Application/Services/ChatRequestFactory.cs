using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed class ChatRequestFactory : IChatRequestFactory
{
    public OpenAiChatRequest CreateProviderRequest(OpenAiChatRequest request, ModelResolution resolution)
    {
        return new OpenAiChatRequest
        {
            Model = resolution.ModelId,
            Messages = request.Messages,
            Stream = request.Stream,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice?.Clone(),
            FunctionTools = request.FunctionTools.ToList(),
            PreviousToolCalls = request.PreviousToolCalls.ToList(),
            ToolResults = request.ToolResults.ToList()
        };
    }
}
