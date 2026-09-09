using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiModelsResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = OpenAiConstants.ModelsListObject;

    [JsonPropertyName("data")]
    public List<OpenAiModel> Data { get; init; } = new();
}
