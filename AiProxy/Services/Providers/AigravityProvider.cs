using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services.Tools;

namespace AiProxy.Services.Providers;

public sealed class AigravityProvider : IChatProvider, IToolAwareChatProvider
{
    private readonly int _modelListTimeoutSeconds = 5;
    private readonly char _modelLabelSeparator = '\t';

    private readonly CliClientToolRunner _toolRunner = new(OpenAiConstants.Providers.Aigravity);

    private readonly ILogger<AigravityProvider> _logger;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ProviderSessionStore _sessionStore;
    private readonly IModelIdCache _modelIdCache;
    private readonly IPromptFileWriter _promptFileWriter;
    private readonly IImageInputResolver _imageInputResolver;

    public AigravityProvider(
        ILogger<AigravityProvider> logger,
        ShellCommandRunner commandRunner,
        ProviderSessionStore sessionStore,
        IModelIdCache modelIdCache,
        IPromptFileWriter promptFileWriter,
        IImageInputResolver imageInputResolver)
    {
        _logger = logger;
        _commandRunner = commandRunner;
        _sessionStore = sessionStore;
        _modelIdCache = modelIdCache;
        _promptFileWriter = promptFileWriter;
        _imageInputResolver = imageInputResolver;
    }

    public string Name => OpenAiConstants.Providers.Aigravity;
    public bool SupportsImages => true;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default)
    {
        return _modelIdCache.GetOrAddAsync(Name, FetchModelIdsAsync, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FetchModelIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _commandRunner.RunCommandAsync("agy", "models", timeoutSeconds: _modelListTimeoutSeconds, cancellationToken: cancellationToken);
            var models = ParseModelIds(output);

            _logger.LogInformation("Fant {ModelCount} modeller fra {ProviderName}", models.Count, Name);
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Klarte ikke å hente modelliste fra {ProviderName}", Name);
            return new List<string>();
        }
    }

    public async Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var images = await _imageInputResolver.ResolveAsync(request.Messages.SelectMany(message => message.Images), cancellationToken);
        var prompt = ImagePromptBuilder.Build(request.Messages, images.Paths);
        var modelId = NormalizeModelId(request.Model);
        var promptFilePath = await _promptFileWriter.WritePromptFileAsync(sessionId, "agy", prompt, cancellationToken);

        _logger.LogInformation("Kjører {ProviderName} for modell {Model} i sesjon {Session}", Name, modelId, sessionId);

        try
        {
            return await ExecuteWithRetryAsync(request, sessionId, promptFilePath, modelId, cancellationToken);
        }
        finally
        {
            _promptFileWriter.TryDelete(promptFilePath);
        }
    }

    /// <summary>
    /// agy har ingen bryter for å fjerne sine egne verktøy, så her styrer bare protokollteksten.
    /// CLI-en kan derfor velge å gjøre jobben selv i stedet for å returnere et klientkall.
    /// </summary>
    public async Task<OpenAiToolExecutionResult> ExecuteWithToolsAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        if (request.Messages.Any(message => message.Images.Count > 0))
            return new OpenAiToolExecutionResult(await ExecuteAsync(request, sessionId, cancellationToken), null);

        var basePrompt = ImagePromptBuilder.Build(ClientToolMessageFilter.WithoutToolExchange(request.Messages), []);

        return await _toolRunner.RunAsync(
            request,
            basePrompt,
            (prompt, ct) => RunPromptAsync(request, sessionId, prompt, ct),
            cancellationToken);
    }

    private async Task<string> RunPromptAsync(OpenAiChatRequest request, string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var modelId = NormalizeModelId(request.Model);
        var promptFilePath = await _promptFileWriter.WritePromptFileAsync(sessionId, "agy", prompt, cancellationToken);

        _logger.LogInformation("Kjører {ProviderName} med klientverktøy for modell {Model} i sesjon {Session}", Name, modelId, sessionId);

        try
        {
            return await ExecuteWithRetryAsync(request, sessionId, promptFilePath, modelId, cancellationToken);
        }
        finally
        {
            _promptFileWriter.TryDelete(promptFilePath);
        }
    }

    private async Task<string> ExecuteWithRetryAsync(
        OpenAiChatRequest request,
        string sessionId,
        string promptFilePath,
        string modelId,
        CancellationToken cancellationToken)
    {
        var sessionResult = _sessionStore.GetOrCreateSessionId(sessionId, Name, () => Guid.NewGuid().ToString());
        var arguments = BuildArguments(promptFilePath, modelId, sessionResult.ProviderSessionId, includeConversationId: true);

        try
        {
            return await _commandRunner.RunCommandAsync("agy", arguments, workingDirectory: request.WorkingDirectory, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsConversationMissingError(ex.Message))
        {
            return await HandleMissingConversationAsync(request, sessionId, promptFilePath, modelId, sessionResult, ex.Message, cancellationToken);
        }
    }

    private async Task<string> HandleMissingConversationAsync(
        OpenAiChatRequest request,
        string sessionId,
        string promptFilePath,
        string modelId,
        ProviderSessionResult sessionResult,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (!sessionResult.WasCreated)
        {
            _logger.LogWarning("{ProviderName}-conversation {ConversationId} finnes ikke lenger, oppretter ny", Name, sessionResult.ProviderSessionId);
            _sessionStore.Reset(sessionId, Name);
            return await ExecuteWithRetryAsync(request, sessionId, promptFilePath, modelId, cancellationToken);
        }

        _logger.LogWarning("{ProviderName} støtter ikke oppretting av conversation med gjennom UUID, kjører uten session-gjenbruk", Name);
        _sessionStore.Reset(sessionId, Name);

        var arguments = BuildArguments(promptFilePath, modelId, string.Empty, includeConversationId: false);
        return await _commandRunner.RunCommandAsync("agy", arguments, workingDirectory: request.WorkingDirectory, cancellationToken: cancellationToken);
    }

    private bool IsConversationMissingError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalizedMessage = message.ToLowerInvariant();

        return (normalizedMessage.Contains("conversation") || normalizedMessage.Contains("session")) &&
            (normalizedMessage.Contains("not found") ||
             normalizedMessage.Contains("does not exist") ||
             normalizedMessage.Contains("could not find") ||
             normalizedMessage.Contains("invalid") ||
             normalizedMessage.Contains("finnes ikke"));
    }

    internal IReadOnlyList<string> ParseModelIds(string output)
    {
        var modelIds = new List<string>();

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var modelId = NormalizeModelId(line);

            if (!string.IsNullOrWhiteSpace(modelId))
                modelIds.Add(modelId);
        }

        return modelIds;
    }

    internal string NormalizeModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;

        var separatorIndex = modelId.IndexOf(_modelLabelSeparator);

        if (separatorIndex < 0)
            return modelId.Trim();

        return modelId[..separatorIndex].Trim();
    }

    internal List<string> BuildArguments(string promptFilePath, string modelId, string providerSessionId, bool includeConversationId)
    {
        var arguments = new List<string>();

        if (includeConversationId && !string.IsNullOrWhiteSpace(providerSessionId))
        {
            arguments.Add("--conversation");
            arguments.Add(providerSessionId);
        }

        arguments.Add("-p");
        arguments.Add($"@{promptFilePath}");

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            arguments.Add("--model");
            arguments.Add(modelId);
        }

        arguments.Add("--output-format");
        arguments.Add("text");

        return arguments;
    }

}
