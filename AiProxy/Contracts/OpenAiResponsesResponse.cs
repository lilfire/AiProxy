using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiResponsesResponse
{
    public OpenAiResponsesResponse(string id, string model, long createdAt)
    {
        Id = id;
        Model = model;
        CreatedAt = createdAt;
    }

    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = OpenAiConstants.ResponseObject;

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = OpenAiConstants.ResponseStatuses.Completed;

    [JsonPropertyName("usage")]
    public OpenAiResponsesUsage Usage { get; init; } = new();

    [JsonPropertyName("output")]
    public List<OpenAiResponsesOutput> Output { get; init; } = new();
}
