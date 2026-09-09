using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Services.Providers;

internal sealed class JsonStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public JsonElement Message { get; set; }

    [JsonPropertyName("event")]
    public JsonElement Event { get; set; }
}
