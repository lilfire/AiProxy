using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiResponsesRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public object? Input { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenAiResponsesTool>? Tools { get; set; }

    /// <summary>Sendes både som strengen "auto" og som objekt, så den må bindes som rå JSON.</summary>
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }
}
