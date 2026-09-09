using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiResponsesOutput
{
    public OpenAiResponsesOutput(List<OpenAiResponsesContent> content)
    {
        Content = content;
    }

    [JsonPropertyName("type")]
    public string Type { get; init; } = OpenAiConstants.ResponseObjectTypes.Message;

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; } = OpenAiConstants.Roles.Assistant;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiResponsesContent>? Content { get; init; }

    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; init; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Input { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    public static OpenAiResponsesOutput CreateFunctionCall(string itemId, string callId, string name, string arguments) =>
        CreateToolCall(itemId, callId, name, arguments, OpenAiConstants.ToolCalls.FunctionType);

    public static OpenAiResponsesOutput CreateToolCall(string itemId, string callId, string name, string input, string toolType) => new([])
    {
        Type = toolType == OpenAiConstants.ToolCalls.CustomType
            ? OpenAiConstants.ResponseObjectTypes.CustomToolCall
            : OpenAiConstants.ResponseObjectTypes.FunctionCall,
        Role = null,
        Content = null,
        Id = itemId,
        CallId = callId,
        Name = name,
        Arguments = toolType == OpenAiConstants.ToolCalls.CustomType ? null : input,
        Input = toolType == OpenAiConstants.ToolCalls.CustomType ? input : null,
        Status = OpenAiConstants.ResponseStatuses.Completed
    };
}
