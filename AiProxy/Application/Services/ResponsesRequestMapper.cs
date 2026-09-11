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
        _logger.LogInformation("Responses input-struktur: {InputStructure}", DescribeInputStructure(request.Input));
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
                .Select(tool => tool.ToCallableTool())
                .OfType<OpenAiFunctionTool>()
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
            var type = GetStringProperty(item, "type");
            if (type != OpenAiConstants.ResponseInputTypes.FunctionCall && type != OpenAiConstants.ResponseInputTypes.CustomToolCall)
                continue;

            var callId = GetStringProperty(item, "call_id");
            var name = GetStringProperty(item, "name");
            var arguments = GetStringProperty(item, type == OpenAiConstants.ResponseInputTypes.CustomToolCall ? "input" : "arguments");
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
            var type = GetStringProperty(item, "type");
            if (type != OpenAiConstants.ResponseInputTypes.FunctionCallOutput && type != OpenAiConstants.ResponseInputTypes.CustomToolCallOutput)
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
            || type == OpenAiConstants.ResponseInputTypes.FunctionCallOutput
            || type == OpenAiConstants.ResponseInputTypes.CustomToolCall
            || type == OpenAiConstants.ResponseInputTypes.CustomToolCallOutput;
    }

    private OpenAiMessage ConvertResponseItemToMessage(JsonElement item)
    {
        var type = GetStringProperty(item, "type");

        if (type == OpenAiConstants.ResponseInputTypes.InputText)
        {
            var text = GetStringProperty(item, "text") ?? string.Empty;
            if (IsImageDataUrl(text))
                return new OpenAiMessage(OpenAiConstants.Roles.User, null)
                {
                    Images = [new OpenAiImageInput(text)]
                };
            return new OpenAiMessage(OpenAiConstants.Roles.User, text);
        }

        // Some OpenAI-compatible clients put content parts directly in input rather than
        // wrapping them in a type=message item. Preserve a standalone image part so provider
        // image routing sees the attachment instead of silently treating it as empty text.
        if (type is OpenAiConstants.ResponseInputTypes.InputImage or "image_url")
        {
            var imageUrl = GetImageUrl(item);
            return new OpenAiMessage(OpenAiConstants.Roles.User, null)
            {
                Images = string.IsNullOrWhiteSpace(imageUrl) ? [] : [new OpenAiImageInput(imageUrl)]
            };
        }

        if (type is "input_file" or "image_file")
        {
            var imageUrl = GetImageFileData(item);
            return new OpenAiMessage(OpenAiConstants.Roles.User, null)
            {
                Images = string.IsNullOrWhiteSpace(imageUrl) ? [] : [new OpenAiImageInput(imageUrl)]
            };
        }

        // OpenCode's Responses adapter emits { role, content } directly and omits
        // type="message". Treat it as a message so multimodal content is not reduced to text.
        if (type == OpenAiConstants.ResponseInputTypes.Message || item.TryGetProperty("role", out _))
        {
            var message = JsonSerializer.Deserialize<OpenAiMessage>(item.GetRawText())
                ?? new OpenAiMessage(OpenAiConstants.Roles.User, string.Empty);
            PromoteImageDataUrlsInContent(item, message);
            return message;
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

    private static string? GetImageUrl(JsonElement item)
    {
        if (!item.TryGetProperty("image_url", out var imageUrl))
            return GetStringProperty(item, "url");

        return imageUrl.ValueKind switch
        {
            JsonValueKind.String => imageUrl.GetString(),
            JsonValueKind.Object when imageUrl.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String => url.GetString(),
            _ => null
        };
    }

    private static bool IsImageDataUrl(string? value) =>
        value?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true;

    private static void PromoteImageDataUrlsInContent(JsonElement item, OpenAiMessage message)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var part in content.EnumerateArray())
        {
            var text = GetStringProperty(part, "text");
            if (IsImageDataUrl(text))
                message.Images.Add(new OpenAiImageInput(text!));
        }
    }

    private static string? GetImageFileData(JsonElement item)
    {
        var data = GetStringProperty(item, "file_data") ?? GetStringProperty(item, "data") ?? GetStringProperty(item, "url");
        if (data?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true)
            return data;

        var fileName = GetStringProperty(item, "filename") ?? GetStringProperty(item, "file_name");
        if (data != null && !data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && fileName != null)
        {
            var mediaType = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => null
            };
            if (mediaType != null)
                return $"data:{mediaType};base64,{data}";
        }

        return null;
    }

    private static string DescribeInputStructure(object? input)
    {
        if (input is not JsonElement element)
            return input?.GetType().Name ?? "null";

        return DescribeElement(element);
    }

    private static string DescribeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Take(8).Select(DescribeElement)) + "]",
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Take(12).Select(property =>
                property.Name is "image_url" or "file_data" or "data" or "url" ? property.Name :
                property.Name == "content" ? "content:" + DescribeElement(property.Value) :
                property.Name == "type" && property.Value.ValueKind == JsonValueKind.String ? "type=" + property.Value.GetString() : property.Name)) + "}",
            JsonValueKind.String => "text",
            _ => element.ValueKind.ToString()
        };
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
