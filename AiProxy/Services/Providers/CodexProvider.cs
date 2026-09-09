using AiProxy.Contracts;

namespace AiProxy.Services.Providers;

public sealed class CodexProvider : IChatProvider
{
    private readonly ILogger<CodexProvider> _logger;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ProviderSessionStore _sessionStore;
    private readonly IPromptFileWriter _promptFileWriter;
    private readonly IImageInputResolver _imageInputResolver;

    private readonly IReadOnlyList<string> _defaultModelIds = new List<string>
    {
        "gpt-5.6-terra",
        "gpt-5.6-luna",
        "gpt-5.5",
        "gpt-5.4-mini"
    };

    public CodexProvider(
        ILogger<CodexProvider> logger,
        ShellCommandRunner commandRunner,
        ProviderSessionStore sessionStore,
        IPromptFileWriter promptFileWriter,
        IImageInputResolver imageInputResolver)
    {
        _logger = logger;
        _commandRunner = commandRunner;
        _sessionStore = sessionStore;
        _promptFileWriter = promptFileWriter;
        _imageInputResolver = imageInputResolver;
    }

    public string Name => OpenAiConstants.Providers.Codex;
    public bool SupportsImages => true;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_defaultModelIds);
    }

    public async Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(request.Messages);
        var promptFilePath = await _promptFileWriter.WritePromptFileAsync(sessionId, "codex", prompt, cancellationToken);
        await using var images = await _imageInputResolver.ResolveAsync(request.Messages.SelectMany(message => message.Images), cancellationToken);

        try
        {
            return await ExecuteWithSessionRecoveryAsync(request, sessionId, promptFilePath, images.Paths, cancellationToken);
        }
        finally
        {
            _promptFileWriter.TryDelete(promptFilePath);
        }
    }

    private async Task<string> ExecuteWithSessionRecoveryAsync(OpenAiChatRequest request, string sessionId, string promptFilePath, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken)
    {
        var prompt = await File.ReadAllTextAsync(promptFilePath, cancellationToken);
        var sessionResult = _sessionStore.GetOrCreateSessionId(sessionId, Name, () => Guid.NewGuid().ToString());
        var arguments = BuildArguments(request, sessionResult.ProviderSessionId, sessionResult.WasCreated, imagePaths);

        _logger.LogInformation("Kjører {ProviderName} for modell {Model} i sesjon {Session}", Name, request.Model, sessionId);

        try
        {
            return await _commandRunner.RunCommandAsync("codex", arguments, stdinInput: prompt, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex) when (!sessionResult.WasCreated && (IsSessionMissingError(ex.Message) || IsThreadMissingError(ex.Message)))
        {
            return await RecreateSessionAndExecuteAsync(request, sessionId, promptFilePath, imagePaths, cancellationToken);
        }
    }

    private async Task<string> RecreateSessionAndExecuteAsync(OpenAiChatRequest request, string sessionId, string promptFilePath, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken)
    {
        _sessionStore.Reset(sessionId, Name);
        _logger.LogWarning("{ProviderName}-sesjon finnes ikke lenger, kjører uten session-gjenbruk", Name);

        var prompt = await File.ReadAllTextAsync(promptFilePath, cancellationToken);
        var arguments = BuildArguments(request, string.Empty, true, imagePaths);

        return await _commandRunner.RunCommandAsync("codex", arguments, stdinInput: prompt, cancellationToken: cancellationToken);
    }

    private List<string> BuildArguments(OpenAiChatRequest request, string providerSessionId, bool isNewSession, IReadOnlyList<string> imagePaths)
    {
        var arguments = new List<string> { "exec" };

        if (!isNewSession)
        {
            arguments.Add("resume");
            arguments.Add(providerSessionId);
        }

        arguments.Add("--model");
        arguments.Add(request.Model);
        arguments.Add("--skip-git-repo-check");
        foreach (var imagePath in imagePaths)
        {
            arguments.Add("--image");
            arguments.Add(imagePath);
        }
        arguments.Add("--");
        arguments.Add("-");

        return arguments;
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

    private bool IsThreadMissingError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalizedMessage = message.ToLowerInvariant();

        return normalizedMessage.Contains("thread/resume") &&
            normalizedMessage.Contains("no rollout found");
    }

    private string BuildPrompt(List<OpenAiMessage> messages)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var message in messages)
        {
            builder.Append($"{message.Role}: {message.Content ?? string.Empty}\n\n");
        }

        return builder.ToString().Trim();
    }
}
