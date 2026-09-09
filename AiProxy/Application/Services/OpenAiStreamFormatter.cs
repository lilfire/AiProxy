using System.Text;
using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class OpenAiStreamFormatter : IOpenAiStreamFormatter
{
    public string FormatChatCompletionChunk(OpenAiChatResponse response)
    {
        return FormatSseDataEvent(JsonSerializer.Serialize(response));
    }

    public string FormatSseEvent(string eventName, string data)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{OpenAiConstants.SseEventPrefix}{eventName}");
        builder.AppendLine($"{OpenAiConstants.SseDataPrefix}{data}");
        builder.AppendLine();
        return builder.ToString();
    }

    public string FormatSseDoneEvent()
    {
        return FormatSseDataEvent(OpenAiConstants.SseDoneData);
    }

    /// <summary>En SSE-hendelse avsluttes av en blank linje, ellers slås linjene sammen hos klienten.</summary>
    private string FormatSseDataEvent(string data)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{OpenAiConstants.SseDataPrefix}{data}");
        builder.AppendLine();
        return builder.ToString();
    }
}
