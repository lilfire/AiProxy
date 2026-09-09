using System.Text;
using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Providers;
using AiProxy.Services.Sessions;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Application.Services;

public sealed class ResponsesService : IResponsesService
{
    private readonly int _streamChunkSize = 256;
    private readonly string _streamingProviderTypeName = nameof(ClaudeProvider);

    private readonly IChatProviderRegistry _registry;
    private readonly IResponsesRequestMapper _requestMapper;
    private readonly IChatRequestFactory _requestFactory;
    private readonly IOpenAiStreamFormatter _streamFormatter;
    private readonly IOpenAiResponseEventBuilder _eventBuilder;
    private readonly ITodoToolBridge _todoToolBridge;
    private readonly TodoOutputProtocol _todoOutputProtocol;
    private readonly ILogger<ResponsesService> _logger;
    private readonly IProviderUsageStore _usageStore;
    private readonly ISessionHistoryStore _sessionHistory;

    public ResponsesService(
        IChatProviderRegistry registry,
        IResponsesRequestMapper requestMapper,
        IChatRequestFactory requestFactory,
        IOpenAiStreamFormatter streamFormatter,
        IOpenAiResponseEventBuilder eventBuilder,
        ITodoToolBridge todoToolBridge,
        TodoOutputProtocol todoOutputProtocol,
        IProviderUsageStore usageStore,
        ISessionHistoryStore sessionHistory,
        ILogger<ResponsesService> logger)
    {
        _registry = registry;
        _requestMapper = requestMapper;
        _requestFactory = requestFactory;
        _streamFormatter = streamFormatter;
        _eventBuilder = eventBuilder;
        _todoToolBridge = todoToolBridge;
        _todoOutputProtocol = todoOutputProtocol;
        _usageStore = usageStore;
        _sessionHistory = sessionHistory;
        _logger = logger;
    }

    public async Task<IResult> ExecuteAsync(OpenAiResponsesRequest request, string sessionId, CancellationToken cancellationToken = default)
    {
        if (_todoToolBridge.IsProxyToolResultFollowUp(request))
            return await CreateEmptyTurnResultAsync(request, sessionId, cancellationToken);

        // Skjemaet må leses her, mens JsonElement-ene fortsatt lever; strømme-callbacken kjører senere.
        var chatRequest = _requestMapper.MapToChatRequest(request);
        var turnId = _sessionHistory.StartTurn(sessionId, "responses", chatRequest.Model, ToSessionMessages(chatRequest));
        try
        {
            var resolution = await _registry.ResolveAsync(chatRequest.Model, cancellationToken);
            EnsureImageSupport(chatRequest, resolution);
            var toolProvider = resolution.Provider as IToolAwareChatProvider;
            var toolMode = toolProvider != null && chatRequest.FunctionTools.Count > 0;
            var todoSchema = toolMode ? new TodoToolSchema(false, string.Empty, false, false) : _todoToolBridge.ReadClientSchema(request);
            var todoOutput = toolMode ? null : _todoOutputProtocol.CreateSession(todoSchema);

            if (todoOutput != null)
                chatRequest.Messages.Add(todoOutput.CreateInstruction());

            var providerRequest = _requestFactory.CreateProviderRequest(chatRequest, resolution);
            _sessionHistory.SetProvider(turnId, resolution.Provider.Name, resolution.DisplayModelId);

            if (toolMode)
            {
                if (request.Stream)
                    return await StreamToolAsync(providerRequest, sessionId, resolution, toolProvider!, turnId, cancellationToken);

                return await ExecuteToolNonStreamingAsync(providerRequest, sessionId, resolution, toolProvider!, turnId, cancellationToken);
            }

            if (!request.Stream)
                return await ExecuteNonStreamingAsync(providerRequest, sessionId, resolution, todoSchema, todoOutput, turnId, cancellationToken);

            var context = new ResponseStreamContext(
                providerRequest,
                sessionId,
                resolution,
                todoSchema,
                todoOutput,
                turnId,
                GenerateResponseId(),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            return StreamAsync(context, cancellationToken);
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
        var call = ToOutputCall(result.ToolCall);
        var response = BuildResponse(resolution.DisplayModelId, result.Text, call);
        _sessionHistory.CompleteTurn(turnId, result.Text);
        return Results.Json(response);
    }

    private async Task<IResult> StreamToolAsync(OpenAiChatRequest request, string sessionId, ModelResolution resolution, IToolAwareChatProvider provider, string turnId, CancellationToken cancellationToken)
    {
        // Validate the fully-buffered M365 answer before Results.Stream begins the HTTP body.  An
        // invalid fence can then be returned by the normal error middleware instead of producing
        // a half-written SSE response that no middleware can repair.
        var result = await provider.ExecuteWithToolsAsync(request, sessionId, cancellationToken);
        _usageStore.RecordCompletedRequest(resolution.Provider.Name);
        return Results.Stream(stream => WriteToolStreamAsync(stream, resolution, result, turnId, cancellationToken), OpenAiConstants.SseContentType);
    }

    private async Task WriteToolStreamAsync(Stream stream, ModelResolution resolution, OpenAiToolExecutionResult result, string turnId, CancellationToken cancellationToken)
    {
        try
        {
            await using var writer = CreateWriter(stream);
            var responseId = GenerateResponseId();
            await WriteStartEventsAsync(writer, stream, responseId, resolution.DisplayModelId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cancellationToken);
            if (!string.IsNullOrEmpty(result.Text))
                await WriteOutputTextDeltasAsync(writer, stream, responseId, result.Text, cancellationToken);
            await WriteMessageDoneEventsAsync(writer, stream, responseId, result.Text, cancellationToken);
            var call = ToOutputCall(result.ToolCall);
            if (call != null)
                await WriteFunctionCallEventsAsync(writer, stream, call, cancellationToken);
            await WriteFinishAsync(writer, stream, responseId, resolution.DisplayModelId, result.Text, call, cancellationToken);
            _sessionHistory.CompleteTurn(turnId, result.Text);
        }
        catch (Exception exception)
        {
            _sessionHistory.FailTurn(turnId, exception);
            throw;
        }
    }

    private static OpenAiTodoCall? ToOutputCall(OpenAiProviderToolCall? call) => call == null ? null :
        new OpenAiTodoCall("fc_" + Guid.NewGuid().ToString("N"), call.CallId, call.Name, call.ArgumentsJson);

    /// <summary>
    /// Klienten har kjørt vårt eget todo-verktøy og sender bare resultatet tilbake. Turen
    /// avsluttes tomt, uten å starte en ny CLI-kjøring.
    /// </summary>
    private async Task<IResult> CreateEmptyTurnResultAsync(OpenAiResponsesRequest request, string sessionId, CancellationToken cancellationToken)
    {
        var turnId = _sessionHistory.StartTurn(sessionId, "responses", request.Model, []);
        try
        {
            await _registry.ResolveAsync(request.Model, cancellationToken);
            _logger.LogInformation("Kortslutter verktøyresultat for todo-kall på modell {Model}", request.Model);

            if (!request.Stream)
            {
                _sessionHistory.CompleteTurn(turnId, string.Empty);
                return Results.Json(BuildResponse(request.Model, string.Empty));
            }

            var responseId = GenerateResponseId();
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return Results.Stream(
                streamWriterCallback: stream => WriteEmptyTurnStreamAsync(stream, request.Model, responseId, createdAt, turnId, cancellationToken),
                contentType: OpenAiConstants.SseContentType);
        }
        catch (Exception exception)
        {
            _sessionHistory.FailTurn(turnId, exception);
            throw;
        }
    }

    private async Task<IResult> ExecuteNonStreamingAsync(
        OpenAiChatRequest request,
        string sessionId,
        ModelResolution resolution,
        TodoToolSchema todoSchema,
        TodoOutputSession? todoOutput,
        string turnId,
        CancellationToken cancellationToken)
    {
        var providerOutput = await resolution.Provider.ExecuteAsync(request, sessionId, cancellationToken);
        _usageStore.RecordCompletedRequest(resolution.Provider.Name);
        var output = ProcessCompleteOutput(todoOutput, providerOutput);
        var todoCall = _todoToolBridge.TryCreateTodoCall(todoSchema);
        var response = BuildResponse(resolution.DisplayModelId, output, todoCall);
        _sessionHistory.CompleteTurn(turnId, output);

        return Results.Json(response);
    }

    private OpenAiResponsesResponse BuildResponse(string model, string output, OpenAiTodoCall? todoCall = null)
    {
        var content = new OpenAiResponsesContent(output);
        var outputItem = new OpenAiResponsesOutput(new List<OpenAiResponsesContent> { content });

        var response = new OpenAiResponsesResponse(GenerateResponseId(), model, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            Output = new List<OpenAiResponsesOutput> { outputItem }
        };

        if (todoCall != null)
            response.Output.Add(OpenAiResponsesOutput.CreateFunctionCall(todoCall.ItemId, todoCall.CallId, todoCall.Name, todoCall.ArgumentsJson));

        return response;
    }

    private IResult StreamAsync(ResponseStreamContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Strømmer responses-svar for modell {Model} i sesjon {Session}", context.Resolution.DisplayModelId, context.SessionId);

        return Results.Stream(
            streamWriterCallback: stream => WriteResponseStreamAsync(stream, context, cancellationToken),
            contentType: OpenAiConstants.SseContentType);
    }

    private async Task WriteResponseStreamAsync(Stream stream, ResponseStreamContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var writer = CreateWriter(stream);

            await WriteStartEventsAsync(writer, stream, context.ResponseId, context.Request.Model, context.CreatedAt, cancellationToken);

            var fullOutput = await WriteProviderOutputAsync(writer, stream, context, cancellationToken);

            _logger.LogInformation("Provider full output collected ({Provider}): {FullOutput}", context.Resolution.Provider.Name, fullOutput);

            await WriteMessageDoneEventsAsync(writer, stream, context.ResponseId, fullOutput, cancellationToken);

            var todoCall = _todoToolBridge.TryCreateTodoCall(context.TodoSchema);

            if (todoCall != null)
                await WriteFunctionCallEventsAsync(writer, stream, todoCall, cancellationToken);

            await WriteFinishAsync(writer, stream, context.ResponseId, context.Request.Model, fullOutput, todoCall, cancellationToken);
            _sessionHistory.CompleteTurn(context.TurnId, fullOutput);
        }
        catch (Exception exception)
        {
            _sessionHistory.FailTurn(context.TurnId, exception);
            throw;
        }
    }

    private async Task WriteEmptyTurnStreamAsync(Stream stream, string model, string responseId, long createdAt, string turnId, CancellationToken cancellationToken)
    {
        try
        {
            await using var writer = CreateWriter(stream);

            await WriteStartEventsAsync(writer, stream, responseId, model, createdAt, cancellationToken);
            await WriteMessageDoneEventsAsync(writer, stream, responseId, string.Empty, cancellationToken);
            await WriteFinishAsync(writer, stream, responseId, model, string.Empty, null, cancellationToken);
            _sessionHistory.CompleteTurn(turnId, string.Empty);
        }
        catch (Exception exception)
        {
            _sessionHistory.FailTurn(turnId, exception);
            throw;
        }
    }

    private async Task<string> WriteProviderOutputAsync(StreamWriter writer, Stream stream, ResponseStreamContext context, CancellationToken cancellationToken)
    {
        var provider = context.Resolution.Provider;

        if (provider.GetType().Name != _streamingProviderTypeName)
        {
            var providerOutput = await provider.ExecuteAsync(context.Request, context.SessionId, cancellationToken);
            _usageStore.RecordCompletedRequest(provider.Name);
            var output = ProcessCompleteOutput(context.TodoOutput, providerOutput);
            _logger.LogInformation("Provider full output ({Provider}): {Output}", provider.Name, output);
            await WriteOutputTextDeltasAsync(writer, stream, context.ResponseId, output, cancellationToken);

            return output;
        }

        var textBuilder = new StringBuilder();

        await provider.ExecuteStreamingAsync(
            context.Request,
            context.SessionId,
            async (chunk, ct) =>
            {
                _logger.LogInformation("Provider chunk ({Provider}): {Chunk}", provider.Name, chunk);
                var visibleChunk = context.TodoOutput?.ProcessChunk(chunk) ?? chunk;

                if (string.IsNullOrEmpty(visibleChunk))
                    return;

                textBuilder.Append(visibleChunk);
                await WriteTextDeltaEventAsync(writer, stream, context.ResponseId, visibleChunk, ct);
            },
            cancellationToken);

        _usageStore.RecordCompletedRequest(provider.Name);

        var finalChunk = context.TodoOutput?.Complete() ?? string.Empty;

        if (!string.IsNullOrEmpty(finalChunk))
        {
            textBuilder.Append(finalChunk);
            await WriteTextDeltaEventAsync(writer, stream, context.ResponseId, finalChunk, cancellationToken);
        }

        return textBuilder.ToString();
    }

    private string ProcessCompleteOutput(TodoOutputSession? todoOutput, string providerOutput)
    {
        if (todoOutput == null)
            return providerOutput;

        return todoOutput.ProcessChunk(providerOutput) + todoOutput.Complete();
    }

    private async Task WriteStartEventsAsync(StreamWriter writer, Stream stream, string responseId, string model, long createdAt, CancellationToken cancellationToken)
    {
        var created = OpenAiConstants.ResponseEventTypes.Created;
        var inProgress = OpenAiConstants.ResponseEventTypes.InProgress;
        var inProgressStatus = OpenAiConstants.ResponseStatuses.InProgress;

        await WriteEventAsync(writer, stream, created, _eventBuilder.CreateEventJson(created, inProgressStatus, responseId, model, createdAt), cancellationToken);
        await WriteEventAsync(writer, stream, inProgress, _eventBuilder.CreateEventJson(inProgress, inProgressStatus, responseId, model, createdAt), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.OutputItemAdded, _eventBuilder.CreateOutputItemAddedEventJson(responseId), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.ContentPartAdded, _eventBuilder.CreateContentPartAddedEventJson(responseId), cancellationToken);
    }

    private async Task WriteMessageDoneEventsAsync(StreamWriter writer, Stream stream, string responseId, string output, CancellationToken cancellationToken)
    {
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.OutputTextDone, _eventBuilder.CreateOutputTextDoneEventJson(responseId, output), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.ContentPartDone, _eventBuilder.CreateContentPartDoneEventJson(responseId, output), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.OutputItemDone, _eventBuilder.CreateOutputItemDoneEventJson(responseId, output), cancellationToken);
    }

    private async Task WriteFunctionCallEventsAsync(StreamWriter writer, Stream stream, OpenAiTodoCall todoCall, CancellationToken cancellationToken)
    {
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.OutputItemAdded, _eventBuilder.CreateFunctionCallItemAddedEventJson(todoCall), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.FunctionCallArgumentsDelta, _eventBuilder.CreateFunctionCallArgumentsDeltaEventJson(todoCall), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.FunctionCallArgumentsDone, _eventBuilder.CreateFunctionCallArgumentsDoneEventJson(todoCall), cancellationToken);
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.OutputItemDone, _eventBuilder.CreateFunctionCallItemDoneEventJson(todoCall), cancellationToken);
    }

    private async Task WriteFinishAsync(StreamWriter writer, Stream stream, string responseId, string model, string output, OpenAiTodoCall? todoCall, CancellationToken cancellationToken)
    {
        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.Completed, _eventBuilder.CreateCompletedEventJson(responseId, model, output, todoCall), cancellationToken);
        await writer.WriteAsync(_streamFormatter.FormatSseDoneEvent().AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task WriteOutputTextDeltasAsync(StreamWriter writer, Stream stream, string responseId, string output, CancellationToken cancellationToken)
    {
        for (var index = 0; index < output.Length; index += _streamChunkSize)
        {
            var chunk = output.Substring(index, Math.Min(_streamChunkSize, output.Length - index));
            await WriteTextDeltaEventAsync(writer, stream, responseId, chunk, cancellationToken);
        }
    }

    private async Task WriteTextDeltaEventAsync(StreamWriter writer, Stream stream, string responseId, string chunk, CancellationToken cancellationToken)
    {
        var textEvent = new OpenAiResponsesStreamEvent
        {
            Type = OpenAiConstants.ResponseEventTypes.OutputTextDelta,
            ItemId = responseId + OpenAiConstants.ResponseItemIdSuffix,
            OutputIndex = 0,
            ContentIndex = 0,
            Delta = chunk
        };

        await WriteEventAsync(writer, stream, OpenAiConstants.ResponseEventTypes.OutputTextDelta, JsonSerializer.Serialize(textEvent), cancellationToken);
    }

    private async Task WriteEventAsync(StreamWriter writer, Stream stream, string eventName, string data, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(_streamFormatter.FormatSseEvent(eventName, data).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private StreamWriter CreateWriter(Stream stream)
    {
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true, bufferSize: 1);
    }

    private string GenerateResponseId()
    {
        return OpenAiConstants.ResponseIdPrefix + Guid.NewGuid().ToString("N");
    }

    private static void EnsureImageSupport(OpenAiChatRequest request, ModelResolution resolution)
    {
        if (request.Messages.Any(message => message.Images.Count > 0) && !resolution.Provider.SupportsImages)
            throw new ImageInputNotSupportedException($"Modell '{resolution.DisplayModelId}' hos {resolution.Provider.Name} støtter ikke bildevedlegg.");
    }

    private static IReadOnlyList<SessionHistoryMessage> ToSessionMessages(OpenAiChatRequest request) =>
        request.Messages.Select(message => new SessionHistoryMessage(message.Role, message.Content ?? string.Empty)).ToArray();
}
