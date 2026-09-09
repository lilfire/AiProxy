using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiTodoWriteArguments
{
    [JsonPropertyName("todos")]
    public List<OpenAiTodoItem> Todos { get; set; } = new();
}
