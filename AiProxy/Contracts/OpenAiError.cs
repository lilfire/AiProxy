using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiError
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "api_error";

    [JsonPropertyName("code")]
    public string? Code { get; init; }
}
