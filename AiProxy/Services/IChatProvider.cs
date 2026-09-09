using AiProxy.Contracts;

namespace AiProxy.Services;

public interface IChatProvider
{
    string Name { get; }
    bool SupportsImages => false;
    Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default);
    Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default);

    async Task ExecuteStreamingAsync(
        OpenAiChatRequest request,
        string sessionId,
        Func<string, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken = default)
    {
        var output = await ExecuteAsync(request, sessionId, cancellationToken);
        await onChunk(output, cancellationToken);
    }
}
