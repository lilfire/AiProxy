using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenAiMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenAiChatTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    /// <summary>Normalized tool declarations used internally by tool-aware providers.</summary>
    [JsonIgnore]
    public List<OpenAiFunctionTool> FunctionTools { get; set; } = new();

    [JsonIgnore]
    public List<OpenAiToolCall> PreviousToolCalls { get; set; } = new();

    [JsonIgnore]
    public List<OpenAiToolResult> ToolResults { get; set; } = new();
}
