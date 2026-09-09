using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiProxy.Configuration;
using AiProxy.Contracts;

namespace AiProxy.Services.Providers;

/// <summary>OpenAI-compatible provider backed by the M365 Copilot chat hub.</summary>
public sealed class M365CopilotProvider : IChatProvider, IToolAwareChatProvider
{
    private const char RecordSeparator = '\u001e';
    private const string HubBaseUrl = "wss://substrate.office.com/m365Copilot/Chathub/";
    private static readonly string[] ModelIds =
    {
        "auto",
        "quick",
        "think-deeper",
        "gpt-5.6-think-deeper",
        "gpt-5.5-quick"
    };
    private static readonly string Variants = string.Join(',', new[]
    {
        "EnableMcpServerWidgets", "feature.EnableMcpServerWidgets", "feature.EnableLuForChatCIQ",
        "feature.enableChatCIQPlugin", "EnableRequestPlugins", "feature.EnableSensitivityLabels",
        "feature.IsCustomEngineCopilotEnabled", "feature.bizchatfluxv3", "feature.enablechatpages",
        "feature.enableCodeCanvas", "feature.IsStreamingModeInChatRequestEnabled", "IncludeSourceAttributionsConcise",
        "SkipPublishEmptyMessage", "feature.EnableDeduplicatingSourceAttributions", "Enable3PActionProgressMessages",
        "feature.enableCitationsForSynthesisData", "feature.enableGenerateGraphicArtOptionsSet", "cdximagen",
        "feature.EnableUpdatedUXForConfirmationDialog", "feature.EnableClientFileURLSupportForOfficeWebPaidCopilot",
        "feature.EnableDesignerEditor", "feature.OfficeWebToHelix", "feature.OfficeDesktopToHelix",
        "feature.M365TeamsHubToHelix", "feature.OwaHubToHelix", "feature.MonarchHubToHelix",
        "feature.Win32OutlookHubToHelix", "feature.MacOutlookHubToHelix", "Agt_bizchat_enableGpt5ForHelix"
    });
    private static readonly string[] CodeInterpreterOptionSets =
    {
        "cwc_code_interpreter", "cwc_code_interpreter_amsfix", "cwc_code_interpreter_citation_fix",
        "code_interpreter_interactive_charts", "code_interpreter_matplotlib_patching"
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };
    // Shell-klienter legger dette til i slutten av brukermeldingen for sin egen agent.
    // M365 Copilot tolker den innkapslede systeminstruksen som prompt-injection og avslår turnen.
    private static readonly Regex PlanModeReminderPattern = new(
        @"\s*<system-reminder>\s*#\s*Plan Mode\s*-\s*System Reminder\b.*?</system-reminder>\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));

    private readonly M365CopilotTokenProvider _tokenProvider;
    private readonly M365CopilotSessionStore _sessionStore;
    private readonly M365CopilotWorkspaceBridge _workspaceBridge;
    private readonly IRuntimeSettings _settings;
    private readonly ILogger<M365CopilotProvider> _logger;

    public M365CopilotProvider(
        M365CopilotTokenProvider tokenProvider,
        M365CopilotSessionStore sessionStore,
        M365CopilotWorkspaceBridge workspaceBridge,
        IRuntimeSettings settings,
        ILogger<M365CopilotProvider> logger)
    {
        _tokenProvider = tokenProvider;
        _sessionStore = sessionStore;
        _workspaceBridge = workspaceBridge;
        _settings = settings;
        _logger = logger;
    }

    public string Name => OpenAiConstants.Providers.M365Copilot;

    // Desktopklienten bruker en separat GPT-V/artifact-opplasting som ikke er del av
    // den rekonstruerte Chathub-kontrakten. Ikke send bilder som tekst/base64 her.
    public bool SupportsImages => false;

    public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_settings.Current.M365Copilot.Enabled ? ModelIds : Array.Empty<string>());

    public Task<string> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(request, sessionId, onChunk: null, cancellationToken);

    public async Task ExecuteStreamingAsync(
        OpenAiChatRequest request,
        string sessionId,
        Func<string, CancellationToken, Task> onChunk,
        CancellationToken cancellationToken = default)
    {
        await ExecuteCoreAsync(request, sessionId, onChunk, cancellationToken);
    }

    public async Task<OpenAiToolExecutionResult> ExecuteWithToolsAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        if (request.FunctionTools.Count == 0)
            return new OpenAiToolExecutionResult(await ExecuteAsync(request, sessionId, cancellationToken), null);

        // Tool mode is intentionally buffered: neither a fence nor a malformed call can leak to
        // the client as assistant text. A read-only workspace snapshot gives the model the
        // project context it needs while declared client tools remain the only write path.
        // Model-authored file blocks are never applied in this mode.
        var workspace = await _workspaceBridge.BuildPromptAsync(
            BuildPrompt(request.Messages), _settings.Current.M365Copilot, cancellationToken);
        var prompt = M365ToolProtocol.AppendInstruction(
            workspace.Prompt, request.FunctionTools, request.ToolChoice,
            request.PreviousToolCalls, request.ToolResults);
        var requiresWriteCall = M365ToolProtocol.RequiresDeclaredWriteCall(request.Messages, request.FunctionTools);
        if (requiresWriteCall)
            prompt += "\n\n<required-local-write>" +
                "The user explicitly requested a local file change. Invoke the declared client write tool now. " +
                "Do not create a cloud document, return a link, or describe a completed change as text." +
                "</required-local-write>";
        var answer = await ExecuteRawAsync(request, sessionId, prompt, cancellationToken);

        if (M365ToolProtocol.TryParse(answer, request.FunctionTools, out var call))
        {
            if (!M365ToolProtocol.IsAllowedByChoice(request.ToolChoice, call!.Name))
                throw new InvalidOperationException("M365 Copilot forsøkte et verktøykall som tool_choice ikke tillater.");

            return new OpenAiToolExecutionResult(string.Empty, call);
        }

        // M365 sometimes substitutes a cloud artifact or a prose answer for a local file write.
        // Retry once with an unambiguous instruction; a second text-only answer must not be
        // presented to the client as though the requested file had been changed.
        if (requiresWriteCall)
        {
            var retryPrompt = prompt + "\n\n<tool-call-required>Return exactly one declared client write-tool call now. No prose, links, or cloud artifacts.</tool-call-required>";
            answer = await ExecuteRawAsync(request, sessionId, retryPrompt, cancellationToken);
            if (M365ToolProtocol.TryParse(answer, request.FunctionTools, out call))
            {
                if (!M365ToolProtocol.IsAllowedByChoice(request.ToolChoice, call!.Name))
                    throw new InvalidOperationException("M365 Copilot forsøkte et verktøykall som tool_choice ikke tillater.");

                return new OpenAiToolExecutionResult(string.Empty, call);
            }

            var fallbackWorkspace = CreateExplicitWriteFallbackWorkspace(request.Messages, workspace);
            if (fallbackWorkspace != null)
            {
                var fallbackPrompt = workspace.Prompt +
                    "\n\n<required-local-file-block>" +
                    "The client tool transport was unavailable. Return only the complete replacement content for the explicitly requested local file in this exact form, without Markdown: " +
                    $"\n===FILE: {fallbackWorkspace.Files.Keys.Single()}===\n<complete content>\n===END FILE===\n" +
                    "Do not create a cloud document or return a link." +
                    "</required-local-file-block>";
                var fallbackAnswer = await ExecuteRawAsync(request, sessionId, fallbackPrompt, cancellationToken);
                var writeResult = await _workspaceBridge.ApplyWritesAsync(fallbackAnswer, fallbackWorkspace, createBackups: false, cancellationToken);
                if (writeResult.WrittenFiles.Count > 0)
                    return new OpenAiToolExecutionResult(writeResult.Answer, null);
            }

            throw new InvalidOperationException("M365 Copilot fullførte ikke det påkrevde lokale skriveverktøykallet.");
        }

        if (M365ToolProtocol.IsToolFence(answer))
            throw new InvalidOperationException("M365 Copilot returnerte et ugyldig eller ikke-deklarert verktøykall.");

        return new OpenAiToolExecutionResult(answer, null);
    }

    private async Task<string> ExecuteCoreAsync(
        OpenAiChatRequest request,
        string clientSessionId,
        Func<string, CancellationToken, Task>? onChunk,
        CancellationToken cancellationToken)
    {
        if (!_settings.Current.M365Copilot.Enabled)
            throw new InvalidOperationException("M365 Copilot-provideren er deaktivert i konfigurasjonen.");

        var prompt = BuildPrompt(request.Messages);
        var workspace = await _workspaceBridge.BuildPromptAsync(prompt, _settings.Current.M365Copilot, cancellationToken);
        var answer = await ExecuteRawAsync(request, clientSessionId, workspace.Prompt, cancellationToken);

        if (!workspace.WritesEnabled)
        {
            if (onChunk != null && !string.IsNullOrWhiteSpace(answer))
                await onChunk(answer, cancellationToken);
            return answer;
        }

        var writeResult = await _workspaceBridge.ApplyWritesAsync(answer, workspace, createBackups: false, cancellationToken);
        if (onChunk != null && !string.IsNullOrWhiteSpace(writeResult.Answer))
            await onChunk(writeResult.Answer, cancellationToken);
        return writeResult.Answer;
    }

    private async Task<string> ExecuteRawAsync(OpenAiChatRequest request, string clientSessionId, string prompt, CancellationToken cancellationToken)
    {
        if (!_settings.Current.M365Copilot.Enabled)
            throw new InvalidOperationException("M365 Copilot-provideren er deaktivert i konfigurasjonen.");

        var token = await _tokenProvider.GetTokenAsync(cancellationToken);
        var claims = GetTokenClaims(token);
        var turn = _sessionStore.GetNextTurn(clientSessionId);
        var requestId = Guid.NewGuid().ToString();

        _logger.LogInformation("Kjører M365 Copilot for modell {Model} i sesjon {Session}, request {RequestId}", request.Model, clientSessionId, requestId);

        using var socket = new ClientWebSocket();
        ConfigureSocket(socket);

        try
        {
            await socket.ConnectAsync(BuildHubUri(claims, token, requestId, clientSessionId, turn.ConversationId), cancellationToken);
            await SendAsync(socket, new { protocol = "json", version = 1 }, cancellationToken);
            await ReceiveHandshakeAsync(socket, cancellationToken);

            await SendChatAsync(socket, request, prompt, requestId, clientSessionId, turn.IsFirstTurn, cancellationToken);
            return await ReadResponseAsync(socket, onChunk: null, cancellationToken, requestId);
        }
        catch (OperationCanceledException)
        {
            await TryStopAsync(socket);
            throw;
        }
    }

    private void ConfigureSocket(ClientWebSocket socket)
    {
        socket.Options.SetRequestHeader("Origin", "https://m365.cloud.microsoft");
        socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (X11; Linux x86_64; rv:148.0) Gecko/20100101 Firefox/148.0");
        socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    private static Uri BuildHubUri(M365TokenClaims claims, string token, string requestId, string sessionId, string conversationId)
    {
        var query = new Dictionary<string, string>
        {
            ["chatsessionid"] = requestId,
            ["clientrequestid"] = requestId,
            ["X-SessionId"] = sessionId,
            ["ConversationId"] = conversationId,
            ["access_token"] = token,
            ["variants"] = Variants,
            ["source"] = "\"officeweb\"",
            ["product"] = "Office",
            ["agentHost"] = "Bizchat.FullScreen",
            ["licenseType"] = "Starter",
            ["agent"] = "web",
            ["scenario"] = "OfficeWebIncludedCopilot"
        };
        var encodedQuery = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri($"{HubBaseUrl}{Uri.EscapeDataString(claims.ObjectId)}@{Uri.EscapeDataString(claims.TenantId)}?{encodedQuery}");
    }

    private async Task SendChatAsync(
        ClientWebSocket socket,
        OpenAiChatRequest request,
        string prompt,
        string requestId,
        string sessionId,
        bool isFirstTurn,
        CancellationToken cancellationToken)
    {
        var arguments = new
        {
            source = "officeweb",
            clientCorrelationId = requestId,
            sessionId,
            optionsSets = CodeInterpreterOptionSets,
            streamingMode = "ConciseWithPadding",
            spokenTextMode = "None",
            options = new { },
            extraExtensionParameters = new { },
            allowedMessageTypes = new[]
            {
                "Chat", "Suggestion", "InternalSearchQuery", "Disengaged", "InternalLoaderMessage", "Progress",
                "RenderCardRequest", "SemanticSerp", "GenerateContentQuery", "SearchQuery", "ConfirmationCard",
                "DeveloperLogs", "EndOfRequest", "ReferencesListComplete", "GeneratedCode"
            },
            sliceIds = Array.Empty<string>(),
            threadLevelGptId = new { },
            traceId = requestId,
            isStartOfSession = isFirstTurn,
            clientInfo = new
            {
                clientPlatform = "mcmcopilot-web", clientAppName = "Office", clientEntrypoint = "mcmcopilot-officeweb",
                clientSessionId = sessionId, clientAppType = "Web", deviceOS = "Linux", deviceType = "Desktop"
            },
            message = new
            {
                author = "user", inputMethod = "Keyboard", text = prompt,
                entityAnnotationTypes = new[] { "People", "File", "Event", "Email", "TeamsMessage" },
                requestId,
                locationInfo = new { timeZoneOffset = 1, timeZone = "Europe/Copenhagen" },
                locale = "en-gb", messageType = "Chat", experienceType = "Default",
                adaptiveCards = Array.Empty<object>(), clientPreferences = new { }
            },
            plugins = new[] { new { Id = "BingWebSearch", Source = "BuiltIn" } },
            isSbsSupported = true,
            tone = GetToneForModel(request.Model),
            renderReferencesBehindEOS = true,
            disconnectBehavior = "continue"
        };
        var chat = new { arguments = new[] { arguments }, invocationId = "0", target = "chat", type = 4 };
        var metrics = new
        {
            arguments = new[] { new { Timestamps = new { ConnectionStart = DateTimeOffset.UtcNow, UserInputStart = DateTimeOffset.UtcNow, ConnectionEstablished = DateTimeOffset.UtcNow, UserInputSubmit = DateTimeOffset.UtcNow } } },
            target = "Metrics",
            type = 1
        };

        var payload = JsonSerializer.Serialize(chat, JsonOptions) + RecordSeparator + JsonSerializer.Serialize(metrics, JsonOptions) + RecordSeparator;
        await SendTextAsync(socket, payload, cancellationToken);
    }

    private static async Task ReceiveHandshakeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var frames = SplitFrames(await ReceiveTextAsync(socket, cancellationToken));
        foreach (var frame in frames)
        {
            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException($"M365 Copilot-handshake feilet: {error.GetString()}");
        }
    }

    private static async Task<string> ReadResponseAsync(ClientWebSocket socket, Func<string, CancellationToken, Task>? onChunk, CancellationToken cancellationToken, string requestId)
    {
        var answer = string.Empty;

        while (socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken);
            if (text.Length == 0)
                break;

            foreach (var frame in SplitFrames(text))
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetInt32() : -1;

                if (type == 6)
                {
                    await SendAsync(socket, new { type = 6 }, cancellationToken);
                    continue;
                }

                if (type is 3 or 7)
                {
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        throw new InvalidOperationException($"M365 Copilot avbrøt forespørselen (request {requestId}): {error.GetString()}");
                    return answer;
                }

                if (type == 2)
                {
                    if (root.TryGetProperty("item", out var item))
                        await AppendMessagesAsync(item, onChunk, cancellationToken, value => answer = Fold(answer, value).Answer, () => answer);
                    return answer;
                }

                if (type == 1 && root.TryGetProperty("target", out var target) && target.GetString() == "update" && root.TryGetProperty("arguments", out var arguments))
                {
                    foreach (var argument in arguments.EnumerateArray())
                    {
                        if (argument.TryGetProperty("writeAtCursor", out var delta) && delta.ValueKind == JsonValueKind.String)
                        {
                            await AppendTextAsync(answer + delta.GetString(), onChunk, cancellationToken, value => answer = value, () => answer);
                        }
                        else
                        {
                            await AppendMessagesAsync(argument, onChunk, cancellationToken, value => answer = Fold(answer, value).Answer, () => answer);
                        }
                    }
                }
            }
        }

        return answer;
    }

    private static async Task AppendMessagesAsync(JsonElement container, Func<string, CancellationToken, Task>? onChunk, CancellationToken cancellationToken, Action<string> setAnswer, Func<string> getAnswer)
    {
        if (!container.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return;

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("author", out var author) || author.GetString() != "bot" ||
                !message.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                (message.TryGetProperty("messageType", out var messageType) && messageType.ValueKind == JsonValueKind.String))
                continue;

            await AppendTextAsync(text.GetString()!, onChunk, cancellationToken, setAnswer, getAnswer);
        }
    }

    private static async Task AppendTextAsync(string candidate, Func<string, CancellationToken, Task>? onChunk, CancellationToken cancellationToken, Action<string> setAnswer, Func<string> getAnswer)
    {
        var folded = Fold(getAnswer(), candidate);
        if (folded.Answer == getAnswer())
            return;

        setAnswer(folded.Answer);
        if (folded.Emitted != null && onChunk != null)
            await onChunk(folded.Emitted, cancellationToken);
    }

    private static (string Answer, string? Emitted) Fold(string current, string candidate)
    {
        if (candidate.Length <= current.Length)
            return (current, null);
        if (candidate.StartsWith(current, StringComparison.Ordinal))
            return (candidate, candidate[current.Length..]);
        return (candidate, null);
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken) =>
        await SendTextAsync(socket, JsonSerializer.Serialize(value, JsonOptions) + RecordSeparator, cancellationToken);

    private static async Task SendTextAsync(ClientWebSocket socket, string value, CancellationToken cancellationToken) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return string.Empty;
            await content.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);
    }

    private static IEnumerable<string> SplitFrames(string payload) => payload.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries);

    private static async Task TryStopAsync(ClientWebSocket socket)
    {
        if (socket.State != WebSocketState.Open)
            return;

        try
        {
            var stop = "{\"arguments\":[{}],\"invocationId\":\"1\",\"target\":\"stop\",\"type\":1}" + RecordSeparator;
            await SendTextAsync(socket, stop, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The upstream socket is already gone; cancellation is still the intended outcome.
        }
    }

    private static M365TokenClaims GetTokenClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            throw new InvalidOperationException("M365 Copilot-tokenet er ikke en gyldig JWT.");

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = document.RootElement;
        return new M365TokenClaims(
            root.GetProperty("oid").GetString() ?? throw new InvalidOperationException("JWT mangler oid."),
            root.GetProperty("tid").GetString() ?? throw new InvalidOperationException("JWT mangler tid."));
    }

    private static string GetToneForModel(string model) => model.ToLowerInvariant() switch
    {
        "quick" => "Gpt_Quick",
        "think-deeper" => "Gpt_Reasoning",
        "claude" or "claude-sonnet" or "claude-sonnet-4.5" => "Claude_Sonnet",
        "claude-sonnet-think-deeper" => "Claude_Sonnet_Reasoning",
        "claude-opus" => "Claude_Opus",
        "gpt-5.5" or "gpt-5.5-quick" => "Gpt_5_5_Chat",
        "gpt-5.5-think-deeper" => "Gpt_5_5_Reasoning",
        "gpt-5.6-think-deeper" => "Gpt_5_6_Reasoning",
        "gpt-5.4" or "gpt-5.4-think-deeper" => "Gpt_5_4_Reasoning",
        "gpt-5.4-quick" => "Gpt_5_4_Quick",
        "gpt-5.3" or "gpt-5.3-quick" => "Gpt_5_3_Quick",
        "gpt-5.3-think-deeper" => "Gpt_5_3_Reasoning",
        "gpt-5.2" or "gpt-5.2-quick" => "Gpt_5_2_Quick",
        "gpt-5.2-think-deeper" => "Gpt_5_2_Reasoning",
        _ when model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) => "Claude_Sonnet",
        _ => "magic"
    };

    private static string BuildPrompt(IEnumerable<OpenAiMessage> messages) => string.Join(
        "\n\n",
        messages.Select(message => $"{message.Role.ToUpperInvariant()}:\n{RemoveClientControlContext(message.Content)}"));

    private static M365WorkspacePrompt? CreateExplicitWriteFallbackWorkspace(
        IReadOnlyList<OpenAiMessage> messages,
        M365WorkspacePrompt workspace)
    {
        var userText = string.Join("\n", messages
            .Where(message => string.Equals(message.Role, OpenAiConstants.Roles.User, StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content));
        var matchingFiles = workspace.Files
            .Where(file => userText.Contains(Path.GetFileName(file.Key), StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();

        // A fallback may replace exactly one existing file named by the user. This preserves the
        // normal tool-first flow while making a cloud link unable to mutate project structure.
        if (matchingFiles.Length != 1)
            return null;

        return new M365WorkspacePrompt(
            workspace.Prompt,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [matchingFiles[0].Key] = matchingFiles[0].Value },
            WritesEnabled: true);
    }

    private static string RemoveClientControlContext(string? content) =>
        string.IsNullOrEmpty(content) ? string.Empty : PlanModeReminderPattern.Replace(content, "\n").Trim();

    private sealed record M365TokenClaims(string ObjectId, string TenantId);
}
