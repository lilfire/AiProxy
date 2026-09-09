using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiChatResponse
{
    public OpenAiChatResponse(string id, string model, string objectType, long created)
    {
        Id = id;
        Object = objectType;
        Model = model;
        Created = created;
    }

    public OpenAiChatResponse(string id, string model)
        : this(id, model, OpenAiConstants.ChatCompletionObject, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    {
    }

    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; }

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; }

    [JsonPropertyName("choices")]
    public List<OpenAiChoice> Choices { get; init; } = new();

    [JsonPropertyName("usage")]
    public OpenAiUsage Usage { get; init; } = new();
}
