using AiProxy.Application.Services;
using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

/// <summary>Broen mellom provider-todoer og OpenCode-klientens todowrite-verktøy.</summary>
public interface ITodoToolBridge
{
    /// <summary>Sann når forespørselen bare bærer verktøyresultatet for vårt eget todo-kall.</summary>
    bool IsProxyToolResultFollowUp(OpenAiResponsesRequest request);

    TodoToolSchema ReadClientSchema(OpenAiResponsesRequest request);

    OpenAiTodoCall? TryCreateTodoCall(TodoToolSchema schema);
}
