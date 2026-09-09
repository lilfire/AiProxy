using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class ResponsesRequestMapper : IResponsesRequestMapper
{
    private readonly ILogger<ResponsesRequestMapper> _logger;

    public ResponsesRequestMapper(ILogger<ResponsesRequestMapper> logger)
    {
        _logger = logger;
    }

    public OpenAiChatRequest MapToChatRequest(OpenAiResponsesRequest request)
    {
        var messages = ExtractMessages(request.Input);

        return new OpenAiChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = request.Stream,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ToolChoice = request.ToolChoice?.Clone(),
            FunctionTools = request.Tools?
                .Where(tool => tool.Type == OpenAiConstants.ToolCalls.FunctionType && !string.IsNullOrWhiteSpace(tool.Name))
                .Select(tool => new OpenAiFunctionTool(tool.Name, tool.Description, tool.Parameters, tool.Strict))
                .ToList() ?? [],
            PreviousToolCalls = ExtractToolCalls(request.Input),
            ToolResults = ExtractToolResults(request.Input)
        };
    }

    private List<OpenAiMessage> ExtractMessages(object? input)
    {
        var messages = RemoveEmptyMessages(TryExtractMessages(input));

        if (messages.Count == 0)
            messages.Add(new OpenAiMessage(OpenAiConstants.Roles.User, string.Empty));

        return messages;
    }

    private static List<OpenAiToolCall> ExtractToolCalls(object? input)
    {
        var result = new List<OpenAiToolCall>();
        if (input is not JsonElement { ValueKind: JsonValueKind.Array } items)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            if (GetStringProperty(item, "type") != OpenAiConstants.ResponseInputTypes.FunctionCall)
                continue;

            var callId = GetStringProperty(item, "call_id");
            var name = GetStringProperty(item, "name");
            var arguments = GetStringProperty(item, "arguments");
            if (!string.IsNullOrWhiteSpace(callId) && !string.IsNullOrWhiteSpace(name) && arguments != null)
                result.Add(new OpenAiToolCall(callId, name, arguments));
        }

        return result;
    }

    private static List<OpenAiToolResult> ExtractToolResults(object? input)
    {
        var result = new List<OpenAiToolResult>();
        if (input is not JsonElement { ValueKind: JsonValueKind.Array } items)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            if (GetStringProperty(item, "type") != OpenAiConstants.ResponseInputTypes.FunctionCallOutput)
                continue;

            var callId = GetStringProperty(item, "call_id");
            if (string.IsNullOrWhiteSpace(callId) || !item.TryGetProperty("output", out var output))
                continue;

            result.Add(new OpenAiToolResult(callId, output.ValueKind == JsonValueKind.String ? output.GetString() ?? string.Empty : output.GetRawText()));
        }

        return result;
    }

    /// <summary>En tom assistenttur ville blitt til "assistant: " i prompten til CLI-en.</summary>
    private List<OpenAiMessage> RemoveEmptyMessages(List<OpenAiMessage> messages)
    {
        var result = new List<OpenAiMessage>();

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Content) || message.Images.Count > 0)
                result.Add(message);
        }

        return result;
    }

    private List<OpenAiMessage> TryExtractMessages(object? input)
    {
        if (input is JsonElement element)
            return ExtractMessagesFromJsonElement(element);

        if (input is string inputString)
            return new List<OpenAiMessage> { new(OpenAiConstants.Roles.User, inputString) };

        return new List<OpenAiMessage>();
    }

    private List<OpenAiMessage> ExtractMessagesFromJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return new List<OpenAiMessage> { new(OpenAiConstants.Roles.User, element.GetString()) };

        if (element.ValueKind != JsonValueKind.Array)
            return new List<OpenAiMessage>();

        var messages = new List<OpenAiMessage>();

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && !IsToolItem(item))
                messages.Add(ConvertResponseItemToMessage(item));
        }

        return messages;
    }

    /// <summary>Verktøykall og verktøyresultater kan ikke bli til prompt-tekst for CLI-ene.</summary>
    private bool IsToolItem(JsonElement item)
    {
        var type = GetStringProperty(item, "type");

        return type == OpenAiConstants.ResponseInputTypes.FunctionCall
            || type == OpenAiConstants.ResponseInputTypes.FunctionCallOutput;
    }

    private OpenAiMessage ConvertResponseItemToMessage(JsonElement item)
    {
        var type = GetStringProperty(item, "type");

        if (type == OpenAiConstants.ResponseInputTypes.InputText)
        {
            var text = GetStringProperty(item, "text") ?? string.Empty;
            return new OpenAiMessage(OpenAiConstants.Roles.User, text);
        }

        if (type == OpenAiConstants.ResponseInputTypes.Message)
        {
            return JsonSerializer.Deserialize<OpenAiMessage>(item.GetRawText())
                ?? new OpenAiMessage(OpenAiConstants.Roles.User, string.Empty);
        }

        var fallbackContent = GetStringProperty(item, "text")
            ?? ExtractTextFromContentValue(item, "content")
            ?? string.Empty;

        _logger.LogWarning("Ukjent input-type {Type} i responses-forespørsel, bruker fallback til brukermelding", type);

        return new OpenAiMessage(OpenAiConstants.Roles.User, fallbackContent);
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.GetString();
    }

    private string ExtractTextFromMessageContent(JsonElement item)
    {
        return ExtractTextFromContentValue(item, "content") ?? string.Empty;
    }

    private string? ExtractTextFromContentValue(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var content))
            return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
            return ConcatenateTextParts(content);

        return null;
    }

    private string ConcatenateTextParts(JsonElement contentArray)
    {
        var parts = new List<string>();

        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                continue;

            var text = GetStringProperty(part, "text");

            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }

        return string.Join("\n", parts);
    }
}
