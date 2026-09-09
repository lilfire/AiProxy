using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiChoice
{
    public OpenAiChoice(int index, OpenAiMessage? message, OpenAiMessage? delta, string? finishReason)
    {
        Index = index;
        Message = message;
        Delta = delta;
        FinishReason = finishReason;
    }

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("message")]
    public OpenAiMessage? Message { get; init; }

    [JsonPropertyName("delta")]
    public OpenAiMessage? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}
