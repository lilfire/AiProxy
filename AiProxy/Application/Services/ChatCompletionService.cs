using System.Text;
using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Sessions;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Application.Services;

public sealed class ChatCompletionService : IChatCompletionService
{
    private readonly IChatProviderRegistry _registry;
    private readonly IChatRequestFactory _requestFactory;
    private readonly IOpenAiStreamFormatter _streamFormatter;
    private readonly ILogger<ChatCompletionService> _logger;
    private readonly IProviderUsageStore _usageStore;
    private readonly ISessionHistoryStore _sessionHistory;
    private readonly string _chunkObject = OpenAiConstants.ChatCompletionChunkObject;
    private readonly string _stopReason = OpenAiConstants.FinishReasons.Stop;

    public ChatCompletionService(
        IChatProviderRegistry registry,
        IChatRequestFactory requestFactory,
        IOpenAiStreamFormatter streamFormatter,
        IProviderUsageStore usageStore,
        ISessionHistoryStore sessionHistory,
        ILogger<ChatCompletionService> logger)
    {
        _registry = registry;
        _requestFactory = requestFactory;
        _streamFormatter = streamFormatter;
        _usageStore = usageStore;
        _sessionHistory = sessionHistory;
        _logger = logger;
    }

    public async Task<IResult> ExecuteAsync(OpenAiChatRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        request.FunctionTools = request.Tools?
            .Select(tool => tool.ToFunctionTool())
            .OfType<OpenAiFunctionTool>()
            .ToList() ?? [];
        PopulateChatToolContext(request);
        var turnId = _sessionHistory.StartTurn(sessionId, "chat/completions", request.Model, ToSessionMessages(request));
        try
        {
            var resolution = await _registry.ResolveAsync(request.Model, cancellationToken);
            EnsureImageSupport(request, resolution);
            var providerRequest = _requestFactory.CreateProviderRequest(request, resolution);
            _sessionHistory.SetProvider(turnId, resolution.Provider.Name, resolution.DisplayModelId);

            if (resolution.Provider is IToolAwareChatProvider toolProvider && providerRequest.FunctionTools.Count > 0)
            {
                if (request.Stream)
                    return await StreamToolAsync(providerRequest, sessionId, resolution, toolProvider, turnId, cancellationToken);

                return await ExecuteToolNonStreamingAsync(providerRequest, sessionId, resolution, toolProvider, turnId, cancellationToken);
            }

            if (request.Stream)
                return await StreamAsync(providerRequest, sessionId, resolution, turnId, cancellationToken);

            return await ExecuteNonStreamingAsync(providerRequest, sessionId, resolution, turnId, cancellationToken);
        }
        catch (Exception exception)
        {
            _sessionHistory.FailTurn(turnId, exception);
            throw;
        }
    }

    private async Task<IResult> ExecuteToolNonStreamingAsync(OpenAiChatRequest request, string sessionId, ModelResolution resolution, IToolAwareChatProvider provider, string turnId, CancellationToken cancellationToken)
    {
        var result = await provider.ExecuteWithToolsAsync(request, sessionId, cancellationToken);
        _usageStore.RecordCompletedRequest(resolution.Provider.Name);
        var message = result.ToolCall == null
            ? new OpenAiMessage(OpenAiConstants.Roles.Assistant, result.Text)
            : CreateToolCallMessage(result.ToolCall);
        var finish = result.ToolCall == null ? _stopReason : "tool_calls";
        _sessionHistory.CompleteTurn(turnId, result.Text);
        return Results.Json(new OpenAiChatResponse(GenerateId(), resolution.DisplayModelId)
        {
            Choices = [new OpenAiChoice(0, message, null, finish)]
        });
    }

    private async Task<IResult> StreamToolAsync(OpenAiChatRequest request, string sessionId, ModelResolution resolution, IToolAwareChatProvider provider, string turnId, CancellationToken cancellationToken)
    {
        var result = await provider.ExecuteWithToolsAsync(request, sessionId, cancellationToken);
        _usageStore.RecordCompletedRequest(resolution.Provider.Name);
        return Results.Stream(async stream =>
        {
            try
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true, bufferSize: 1);
                await WriteChunkAsync(writer, stream, CreateChunk(resolution.DisplayModelId, new OpenAiMessage(OpenAiConstants.Roles.Assistant, null), null), cancellationToken);
                if (result.ToolCall == null)
                {
                    if (!string.IsNullOrEmpty(result.Text))
                        await WriteChunkAsync(writer, stream, CreateChunk(resolution.DisplayModelId, new OpenAiMessage(string.Empty, result.Text), null), cancellationToken);
                    await WriteChunkAsync(writer, stream, CreateChunk(resolution.DisplayModelId, new OpenAiMessage(string.Empty, null), _stopReason), cancellationToken);
                }
                else
                {
                    await WriteChunkAsync(writer, stream, CreateChunk(resolution.DisplayModelId, CreateToolCallMessage(result.ToolCall, includeStreamIndex: true), null), cancellationToken);
                    await WriteChunkAsync(writer, stream, CreateChunk(resolution.DisplayModelId, new OpenAiMessage(string.Empty, null), "tool_calls"), cancellationToken);
                }
                _sessionHistory.CompleteTurn(turnId, result.Text);
                await writer.WriteAsync(_streamFormatter.FormatSseDoneEvent().AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _sessionHistory.FailTurn(turnId, exception);
                throw;
            }
        }, OpenAiConstants.SseContentType);
    }

    private static OpenAiMessage CreateToolCallMessage(OpenAiProviderToolCall call, bool includeStreamIndex = false) =>
        new(OpenAiConstants.Roles.Assistant, null)
        {
            ToolCalls = [new OpenAiChatToolCall
            {
                Index = includeStreamIndex ? 0 : null,
                Id = call.CallId,
                Function = new OpenAiChatToolFunction { Name = call.Name, Arguments = call.ArgumentsJson }
            }]
        };

    private static void PopulateChatToolContext(OpenAiChatRequest request)
    {
        foreach (var message in request.Messages)
        {
            if (message.ToolCalls != null)
                request.PreviousToolCalls.AddRange(message.ToolCalls
                    .Where(call => call.Type == OpenAiConstants.ToolCalls.FunctionType && !string.IsNullOrWhiteSpace(call.Id) && !string.IsNullOrWhiteSpace(call.Function.Name))
                    .Select(call => new OpenAiToolCall(call.Id, call.Function.Name, call.Function.Arguments)));
            if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.ToolCallId))
                request.ToolResults.Add(new OpenAiToolResult(message.ToolCallId, message.Content ?? string.Empty));
        }
    }

    private async Task<IResult> ExecuteNonStreamingAsync(OpenAiChatRequest request, string sessionId, ModelResolution resolution, string turnId, CancellationToken cancellationToken)
    {
        var output = await resolution.Provider.ExecuteAsync(request, sessionId, cancellationToken);
        _usageStore.RecordCompletedRequest(resolution.Provider.Name);
        _logger.LogInformation("Provider full output ({Provider}): {Output}", resolution.Provider.Name, output);
        var response = BuildResponse(resolution.DisplayModelId, output);
        _sessionHistory.CompleteTurn(turnId, output);

        return Results.Json(response);
    }

    private OpenAiChatResponse BuildResponse(string model, string output)
    {
        var message = new OpenAiMessage(OpenAiConstants.Roles.Assistant, output);
        var choice = new OpenAiChoice(0, message, null, _stopReason);
        var response = new OpenAiChatResponse(GenerateId(), model)
        {
            Choices = new List<OpenAiChoice> { choice }
        };

        return response;
    }

    private async Task<IResult> StreamAsync(OpenAiChatRequest request, string sessionId, ModelResolution resolution, string turnId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Strømmer chat-svar for modell {Model} i sesjon {Session}", resolution.DisplayModelId, sessionId);

        return Results.Stream(
            streamWriterCallback: async stream =>
            {
                try
                {
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true, bufferSize: 1);
                    var fullOutput = new StringBuilder();

                    var startChunk = CreateChunk(request.Model, new OpenAiMessage(OpenAiConstants.Roles.Assistant, null), null);
                    await WriteChunkAsync(writer, stream, startChunk, cancellationToken);

                    await resolution.Provider.ExecuteStreamingAsync(
                        request,
                        sessionId,
                        async (chunk, ct) =>
                        {
                            _logger.LogInformation("Provider chunk ({Provider}): {Chunk}", resolution.Provider.Name, chunk);
                            fullOutput.Append(chunk);
                            var contentChunk = CreateChunk(request.Model, new OpenAiMessage(string.Empty, chunk), null);
                            await WriteChunkAsync(writer, stream, contentChunk, ct);
                        },
                        cancellationToken);

                    _usageStore.RecordCompletedRequest(resolution.Provider.Name);
                    _sessionHistory.CompleteTurn(turnId, fullOutput.ToString());

                    var stopChunk = CreateChunk(request.Model, new OpenAiMessage(string.Empty, null), _stopReason);
                    await WriteChunkAsync(writer, stream, stopChunk, cancellationToken);

                    await writer.WriteAsync(_streamFormatter.FormatSseDoneEvent().AsMemory(), cancellationToken);
                    await writer.FlushAsync(cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    _sessionHistory.FailTurn(turnId, exception);
                    throw;
                }
            },
            contentType: OpenAiConstants.SseContentType);
    }

    private async Task WriteChunkAsync(StreamWriter writer, Stream stream, OpenAiChatResponse chunk, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(_streamFormatter.FormatChatCompletionChunk(chunk).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private OpenAiChatResponse CreateChunk(string model, OpenAiMessage delta, string? finishReason)
    {
        var choice = new OpenAiChoice(0, null, delta, finishReason);

        return new OpenAiChatResponse(GenerateId(), model, _chunkObject, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            Choices = new List<OpenAiChoice> { choice }
        };
    }

    private string GenerateId()
    {
        return OpenAiConstants.ChatCompletionIdPrefix + Guid.NewGuid().ToString("N");
    }

    private static void EnsureImageSupport(OpenAiChatRequest request, ModelResolution resolution)
    {
        if (request.Messages.Any(message => message.Images.Count > 0) && !resolution.Provider.SupportsImages)
            throw new ImageInputNotSupportedException($"Modell '{resolution.DisplayModelId}' hos {resolution.Provider.Name} støtter ikke bildevedlegg.");
    }

    private static IReadOnlyList<SessionHistoryMessage> ToSessionMessages(OpenAiChatRequest request) =>
        request.Messages.Select(message => new SessionHistoryMessage(message.Role, message.Content ?? string.Empty)).ToArray();
}
