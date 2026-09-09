using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

[JsonConverter(typeof(OpenAiMessageJsonConverter))]
public class OpenAiMessage
{
    public OpenAiMessage(string role, string? content)
    {
        Role = role;
        Content = content;
    }

    [JsonPropertyName("role")]
    public string Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiChatToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>Normaliserte bildevedlegg. De serialiseres gjennom content-konverteren.</summary>
    [JsonIgnore]
    public List<OpenAiImageInput> Images { get; init; } = new();
}

public sealed class OpenAiChatToolCall
{
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = OpenAiConstants.ToolCalls.FunctionType;

    [JsonPropertyName("function")]
    public OpenAiChatToolFunction Function { get; init; } = new();
}

public sealed class OpenAiChatToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "{}";
}
