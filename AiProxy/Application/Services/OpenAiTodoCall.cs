namespace AiProxy.Application.Services;

/// <summary>Et ferdig klient-eid verktøykall klart til å skrives som Responses-hendelser.</summary>
public sealed record OpenAiTodoCall(
    string ItemId,
    string CallId,
    string Name,
    string ArgumentsJson,
    string Type = AiProxy.Contracts.OpenAiConstants.ToolCalls.FunctionType);
