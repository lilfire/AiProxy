using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiErrorResponse
{
    public OpenAiErrorResponse(OpenAiError error)
    {
        Error = error;
    }

    [JsonPropertyName("error")]
    public OpenAiError Error { get; init; }
}
