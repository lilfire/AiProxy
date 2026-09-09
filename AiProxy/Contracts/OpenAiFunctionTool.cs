using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

/// <summary>A normalized client-declared function.  Providers must never infer tools from model text.</summary>
public sealed class OpenAiFunctionTool
{
    public OpenAiFunctionTool(string name, string? description, JsonElement? parameters, bool? strict = null, string type = OpenAiConstants.ToolCalls.FunctionType)
    {
        Name = name;
        Description = description;
        Parameters = parameters?.Clone();
        Strict = strict;
        Type = type;
    }

    public string Name { get; }
    public string? Description { get; }
    public JsonElement? Parameters { get; }
    public bool? Strict { get; }
    public string Type { get; }
}

/// <summary>Chat Completions nests a function declaration below the <c>function</c> property.</summary>
public sealed class OpenAiChatTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public OpenAiChatFunction? Function { get; set; }

    public OpenAiFunctionTool? ToFunctionTool() =>
        Type == OpenAiConstants.ToolCalls.FunctionType && !string.IsNullOrWhiteSpace(Function?.Name)
            ? new OpenAiFunctionTool(Function.Name, Function.Description, Function.Parameters, Function.Strict)
            : null;
}

public sealed class OpenAiChatFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}

public sealed record OpenAiToolCall(string Id, string Name, string ArgumentsJson);

public sealed record OpenAiToolResult(string CallId, string Output);

/// <summary>A completed request by a provider for the client to run one declared function.</summary>
public sealed record OpenAiProviderToolCall(
    string CallId,
    string Name,
    string ArgumentsJson,
    string Type = OpenAiConstants.ToolCalls.FunctionType);
