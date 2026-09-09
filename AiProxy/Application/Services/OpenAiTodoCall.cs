namespace AiProxy.Application.Services;

/// <summary>Et ferdig verktøykall klart til å skrives som function_call-hendelser.</summary>
public sealed record OpenAiTodoCall(string ItemId, string CallId, string Name, string ArgumentsJson);
