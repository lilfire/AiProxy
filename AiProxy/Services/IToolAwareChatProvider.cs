using AiProxy.Contracts;

namespace AiProxy.Services;

/// <summary>
/// A provider which can translate a client-declared OpenAI function into its native protocol.
/// It returns a request for the client to execute; AiProxy never executes it.
/// </summary>
public interface IToolAwareChatProvider
{
    Task<OpenAiToolExecutionResult> ExecuteWithToolsAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default);
}

public sealed record OpenAiToolExecutionResult(string Text, OpenAiProviderToolCall? ToolCall);
