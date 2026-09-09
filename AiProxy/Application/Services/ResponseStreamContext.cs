using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed record ResponseStreamContext(
    OpenAiChatRequest Request,
    string SessionId,
    ModelResolution Resolution,
    TodoToolSchema TodoSchema,
    TodoOutputSession? TodoOutput,
    string TurnId,
    string ResponseId,
    long CreatedAt);
