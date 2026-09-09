using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

/// <summary>En todo-oppgave slik klientens todowrite-verktøy forventer den.</summary>
public class OpenAiTodoItem
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Priority { get; set; }
}
