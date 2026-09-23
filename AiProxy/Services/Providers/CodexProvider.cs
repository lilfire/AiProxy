using AiProxy.Contracts;
using AiProxy.Services.Tools;

namespace AiProxy.Services.Providers;

public sealed class CodexProvider : IChatProvider, IToolAwareChatProvider
{
    private readonly ILogger<CodexProvider> _logger;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ProviderSessionStore _sessionStore;
    private readonly IPromptFileWriter _promptFileWriter;
    private readonly IImageInputResolver _imageInputResolver;
    private readonly CodexModelCatalog _modelCatalog;
    private readonly CliClientToolRunner _toolRunner = new(OpenAiConstants.Providers.Codex);

    public CodexProvider(
        ILogger<CodexProvider> logger,
        ShellCommandRunner commandRunner,
        ProviderSessionStore sessionStore,
        IPromptFileWriter promptFileWriter,
        IImageInputResolver imageInputResolver,
        CodexModelCatalog modelCatalog)
    {
        _logger = logger;
        _commandRunner = commandRunner;
        _sessionStore = sessionStore;
        _promptFileWriter = promptFileWriter;
        _imageInputResolver = imageInputResolver;
        _modelCatalog = modelCatalog;
    }

    public string Name => OpenAiConstants.Providers.Codex;
    public bool SupportsImages => true;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default) =>
        _modelCatalog.GetModelIdsAsync(cancellationToken);

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

    public async Task<OpenAiToolExecutionResult> ExecuteWithToolsAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        if (request.Messages.Any(message => message.Images.Count > 0))
            return new OpenAiToolExecutionResult(await ExecuteAsync(request, sessionId, cancellationToken), null);

        ClientToolManifest.LogProviderManifest(Name, request.FunctionTools, _logger);
        var basePrompt = BuildPrompt(ClientToolMessageFilter.WithoutToolExchange(request.Messages));

        return await _toolRunner.RunAsync(
            request,
            basePrompt,
            (prompt, ct) => RunPromptAsync(request, sessionId, prompt, ct),
            cancellationToken);
    }

    private async Task<string> RunPromptAsync(OpenAiChatRequest request, string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var promptFilePath = await _promptFileWriter.WritePromptFileAsync(sessionId, "codex", prompt, cancellationToken);

        try
        {
            return await ExecuteWithSessionRecoveryAsync(request, sessionId, promptFilePath, [], cancellationToken);
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
            return await _commandRunner.RunCommandAsync("codex", arguments, workingDirectory: request.WorkingDirectory, stdinInput: prompt, cancellationToken: cancellationToken);
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

        return await _commandRunner.RunCommandAsync("codex", arguments, workingDirectory: request.WorkingDirectory, stdinInput: prompt, cancellationToken: cancellationToken);
    }

    internal List<string> BuildArguments(OpenAiChatRequest request, string providerSessionId, bool isNewSession, IReadOnlyList<string> imagePaths)
    {
        // OpenCode can ask its selected provider to update global instructions and skills
        // outside the project. `--add-dir` is not honoured by Codex' non-interactive
        // filesystem tool on Windows, so use the same unrestricted provider policy
        // already used for Claude.
        // Codex har ingen måte å fjerne sine egne verktøy. Read-only-sandkassen er det nærmeste:
        // den hindrer CLI-en fra å skrive selv, så endringer må gå gjennom klientens verktøy.
        var arguments = request.FunctionTools.Count > 0
            ? new List<string> { "exec", "--sandbox", "read-only" }
            : new List<string> { "exec", "--dangerously-bypass-approvals-and-sandbox" };

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
