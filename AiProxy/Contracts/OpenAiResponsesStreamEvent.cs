using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiResponsesStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("item_id")]
    public string? ItemId { get; init; }

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }

    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }

    [JsonPropertyName("delta")]
    public string? Delta { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
