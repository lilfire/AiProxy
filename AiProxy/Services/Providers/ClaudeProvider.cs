using System.Text;
using AiProxy.Contracts;

namespace AiProxy.Services.Providers;

public sealed class ClaudeProvider : IChatProvider
{
    private readonly ILogger<ClaudeProvider> _logger;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ProviderSessionStore _sessionStore;
    private readonly IPromptFileWriter _promptFileWriter;
    private readonly ITodoSnapshotStore _todoStore;

    private readonly IReadOnlyList<string> _defaultModelIds;
    private readonly string _executableName = "claude";
    private readonly string _promptFilePrefix = "claude";

    public ClaudeProvider(
        ILogger<ClaudeProvider> logger,
        ShellCommandRunner commandRunner,
        ProviderSessionStore sessionStore,
        IPromptFileWriter promptFileWriter,
        ITodoSnapshotStore todoStore)
    {
        _logger = logger;
        _commandRunner = commandRunner;
        _sessionStore = sessionStore;
        _promptFileWriter = promptFileWriter;
        _todoStore = todoStore;
        _defaultModelIds = new List<string>
        {
            "opus",
            "sonnet",
            "haiku",
            "fable"
        };
    }

    public string Name => OpenAiConstants.Providers.Claude;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_defaultModelIds);
    }

    public async Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        var textBuilder = new StringBuilder();

        await ExecuteStreamingAsync(
            request,
            sessionId,
            (chunk, _) =>
            {
                textBuilder.Append(chunk);
                return Task.CompletedTask;
            },
            cancellationToken);

        return textBuilder.ToString();
    }

    public async Task ExecuteStreamingAsync(
        OpenAiChatRequest request,
        string sessionId,
        Func<string, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(request.Messages);
        var promptFilePath = await _promptFileWriter.WritePromptFileAsync(sessionId, _promptFilePrefix, prompt, cancellationToken);

        try
        {
            await ExecuteWithSessionRecoveryStreamingAsync(request, sessionId, promptFilePath, onChunk, cancellationToken);
        }
        finally
        {
            _promptFileWriter.TryDelete(promptFilePath);
        }
    }

    internal List<string> BuildArguments(OpenAiChatRequest request, string promptFilePath, string providerSessionId, bool isNewSession)
    {
        var arguments = new List<string>();

        if (isNewSession)
        {
            arguments.Add("--session-id");
            arguments.Add(providerSessionId);
        }
        else
        {
            arguments.Add("--resume");
            arguments.Add(providerSessionId);
        }

        arguments.Add("-p");
        arguments.Add($"@{promptFilePath}");
        arguments.Add("--model");
        arguments.Add(request.Model);
        arguments.Add("--output-format");
        arguments.Add("stream-json");
        arguments.Add("--include-partial-messages");
        arguments.Add("--verbose");
        arguments.Add("--dangerously-skip-permissions");

        return arguments;
    }

    internal async Task<string> ExtractAssistantTextAsync(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return output;

        var textBuilder = new StringBuilder();
        var extractor = new ClaudeStreamTextExtractor(
            (chunk, _) =>
            {
                textBuilder.Append(chunk);
                return Task.CompletedTask;
            },
            _todoStore);

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (!await extractor.ProcessLineAsync(line, CancellationToken.None))
                return output.Trim();
        }

        return textBuilder.ToString().Trim();
    }

    private async Task ExecuteWithSessionRecoveryStreamingAsync(
        OpenAiChatRequest request,
        string sessionId,
        string promptFilePath,
        Func<string, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken)
    {
        var sessionResult = _sessionStore.GetOrCreateSessionId(sessionId, Name, () => Guid.NewGuid().ToString());
        var arguments = BuildArguments(request, promptFilePath, sessionResult.ProviderSessionId, sessionResult.WasCreated);
        var extractor = new ClaudeStreamTextExtractor(onChunk, _todoStore);

        _logger.LogInformation("Strømmer {ProviderName} for modell {Model} i sesjon {Session}", Name, request.Model, sessionId);

        try
        {
            await _commandRunner.RunCommandStreamingAsync(
                _executableName,
                arguments,
                async (line, ct) => { await extractor.ProcessLineAsync(line, ct); },
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex) when (!sessionResult.WasCreated && IsSessionMissingError(ex.Message))
        {
            await RecreateSessionAndStreamAsync(request, sessionId, promptFilePath, onChunk, cancellationToken);
        }
    }

    private async Task RecreateSessionAndStreamAsync(
        OpenAiChatRequest request,
        string sessionId,
        string promptFilePath,
        Func<string, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken)
    {
        _sessionStore.Reset(sessionId, Name);
        _todoStore.Clear();

        var newSessionId = _sessionStore.GetOrCreateSessionId(sessionId, Name, () => Guid.NewGuid().ToString()).ProviderSessionId;
        _logger.LogWarning("{ProviderName}-sesjon finnes ikke lenger, oppretter ny sesjon {ProviderSessionId}", Name, newSessionId);

        var arguments = BuildArguments(request, promptFilePath, newSessionId, true);
        var extractor = new ClaudeStreamTextExtractor(onChunk, _todoStore);

        await _commandRunner.RunCommandStreamingAsync(
            _executableName,
            arguments,
            async (line, ct) => { await extractor.ProcessLineAsync(line, ct); },
            cancellationToken: cancellationToken);
    }

    private bool IsSessionMissingError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalizedMessage = message.ToLowerInvariant();

        return normalizedMessage.Contains("session") &&
            (normalizedMessage.Contains("not found") ||
             normalizedMessage.Contains("does not exist") ||
             normalizedMessage.Contains("could not find") ||
             normalizedMessage.Contains("invalid") ||
             normalizedMessage.Contains("finnes ikke"));
    }

    private string BuildPrompt(List<OpenAiMessage> messages)
    {
        var builder = new StringBuilder();

        foreach (var message in messages)
        {
            builder.Append($"{message.Role}: {message.Content ?? string.Empty}\n\n");
        }

        return builder.ToString().Trim();
    }
}
