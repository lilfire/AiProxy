using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiResponsesUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}
