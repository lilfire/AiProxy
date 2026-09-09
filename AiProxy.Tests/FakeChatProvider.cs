using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Tests;

internal sealed class FakeChatProvider : IChatProvider
{
    private readonly string _name;
    private readonly IReadOnlyList<string> _modelIds;
    private readonly string _response;

    public FakeChatProvider(string name, IReadOnlyList<string> modelIds, string response)
    {
        _name = name;
        _modelIds = modelIds;
        _response = response;
    }

    public string Name => _name;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_modelIds);
    }

    public Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        LastSessionId = sessionId;
        return Task.FromResult(_response);
    }

    public async Task ExecuteStreamingAsync(
        OpenAiChatRequest request,
        string sessionId,
        Func<string, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        LastSessionId = sessionId;

        foreach (var chunk in _response.Chunk(5))
        {
            await onChunk(chunk, cancellationToken);
        }
    }

    public OpenAiChatRequest? LastRequest { get; private set; }
    public string? LastSessionId { get; private set; }
}

internal sealed class FakeToolAwareChatProvider : IChatProvider, IToolAwareChatProvider
{
    private readonly IReadOnlyList<string> _modelIds;
    private readonly OpenAiToolExecutionResult _result;

    public FakeToolAwareChatProvider(IReadOnlyList<string> modelIds, OpenAiToolExecutionResult result)
    {
        _modelIds = modelIds;
        _result = result;
    }

    public string Name => "M365";
    public OpenAiChatRequest? LastToolRequest { get; private set; }

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_modelIds);

    public Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult("Unexpected text execution");

    public Task<OpenAiToolExecutionResult> ExecuteWithToolsAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        LastToolRequest = request;
        return Task.FromResult(_result);
    }
}

internal static class StringExtensions
{
    public static IEnumerable<string> Chunk(this string value, int chunkSize)
    {
        for (var index = 0; index < value.Length; index += chunkSize)
        {
            yield return value.Substring(index, Math.Min(chunkSize, value.Length - index));
        }
    }
}
