using System.Text.RegularExpressions;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Services;

public sealed partial class GrokProvider : IChatProvider
{
    private readonly int _modelListTimeoutSeconds = 5;

    private readonly ILogger<GrokProvider> _logger;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ProviderSessionStore _sessionStore;
    private readonly IModelIdCache _modelIdCache;
    private readonly IPromptFileWriter _promptFileWriter;

    public GrokProvider(
        ILogger<GrokProvider> logger,
        ShellCommandRunner commandRunner,
        ProviderSessionStore sessionStore,
        IModelIdCache modelIdCache,
        IPromptFileWriter promptFileWriter)
    {
        _logger = logger;
        _commandRunner = commandRunner;
        _sessionStore = sessionStore;
        _modelIdCache = modelIdCache;
        _promptFileWriter = promptFileWriter;
    }

    public string Name => OpenAiConstants.Providers.Grok;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default)
    {
        return _modelIdCache.GetOrAddAsync(Name, FetchModelIdsAsync, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FetchModelIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _commandRunner.RunCommandAsync("grok", "models", timeoutSeconds: _modelListTimeoutSeconds, cancellationToken: cancellationToken);
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
        var prompt = BuildPrompt(request.Messages);
        var promptFilePath = await _promptFileWriter.WritePromptFileAsync(sessionId, "grok", prompt, cancellationToken);

        try
        {
            return await ExecuteWithSessionRecoveryAsync(request, sessionId, promptFilePath, cancellationToken);
        }
        finally
        {
            _promptFileWriter.TryDelete(promptFilePath);
        }
    }

    private async Task<string> ExecuteWithSessionRecoveryAsync(OpenAiChatRequest request, string sessionId, string promptFilePath, CancellationToken cancellationToken)
    {
        var sessionResult = _sessionStore.GetOrCreateSessionId(sessionId, Name, () => Guid.NewGuid().ToString());
        var arguments = BuildArguments(request, promptFilePath, sessionResult.ProviderSessionId, sessionResult.WasCreated);

        _logger.LogInformation("Kjører {ProviderName} for modell {Model} i sesjon {Session}", Name, request.Model, sessionId);

        try
        {
            return await _commandRunner.RunCommandAsync("grok", arguments, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex) when (!sessionResult.WasCreated && IsSessionMissingError(ex.Message))
        {
            return await RecreateSessionAndExecuteAsync(request, sessionId, promptFilePath, cancellationToken);
        }
    }

    private async Task<string> RecreateSessionAndExecuteAsync(OpenAiChatRequest request, string sessionId, string promptFilePath, CancellationToken cancellationToken)
    {
        _sessionStore.Reset(sessionId, Name);

        var newSessionId = _sessionStore.GetOrCreateSessionId(sessionId, Name, () => Guid.NewGuid().ToString()).ProviderSessionId;
        _logger.LogWarning("{ProviderName}-sesjon finnes ikke lenger, oppretter ny sesjon {ProviderSessionId}", Name, newSessionId);

        var arguments = BuildArguments(request, promptFilePath, newSessionId, true);

        return await _commandRunner.RunCommandAsync("grok", arguments, cancellationToken: cancellationToken);
    }

    private List<string> BuildArguments(OpenAiChatRequest request, string promptFilePath, string providerSessionId, bool isNewSession)
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

        arguments.Add("--prompt-file");
        arguments.Add(promptFilePath);
        arguments.Add("-m");
        arguments.Add(request.Model);
        arguments.Add("--no-auto-update");
        arguments.Add("--no-alt-screen");
        arguments.Add("--output-format");
        arguments.Add("plain");

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

    private IReadOnlyList<string> ParseModelIds(string output)
    {
        var models = new List<string>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var inAvailableSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith("Available models:", StringComparison.OrdinalIgnoreCase))
            {
                inAvailableSection = true;
                continue;
            }

            if (!inAvailableSection)
                continue;

            var match = MatchGrokModelLine(line);
            if (match.Success)
                models.Add(match.Groups["model"].Value.Trim());
        }

        return models;
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

    private Match MatchGrokModelLine(string line)
    {
        return GrokModelLineRegex().Match(line);
    }

    [GeneratedRegex(@"^\s*[*\-\u2022\u25CF]\s*(?<model>[\w\.\-]+)", RegexOptions.Compiled)]
    private static partial Regex GrokModelLineRegex();
}
