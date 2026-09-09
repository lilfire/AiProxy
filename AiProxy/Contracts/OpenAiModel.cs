using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiModel
{
    public OpenAiModel(string id, string ownedBy)
    {
        Id = id;
        OwnedBy = ownedBy;
    }

    public OpenAiModel(string id, string ownedBy, long created)
        : this(id, ownedBy)
    {
        Created = created;
    }

    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = OpenAiConstants.ModelObject;

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; init; }
}
