using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

public class OpenAiResponsesContent
{
    public OpenAiResponsesContent(string text)
    {
        Text = text;
    }

    [JsonPropertyName("type")]
    public string Type { get; init; } = OpenAiConstants.ResponseObjectTypes.OutputText;

    [JsonPropertyName("text")]
    public string Text { get; init; }
}
