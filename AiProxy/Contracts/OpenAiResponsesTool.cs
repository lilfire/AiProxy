using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

/// <summary>Verktøydeklarasjon slik Responses-API-et sender den: flat, ikke nøstet under "function".</summary>
public class OpenAiResponsesTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}
