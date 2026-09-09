using System.Text;
using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

public interface IOpenAiStreamFormatter
{
    string FormatChatCompletionChunk(OpenAiChatResponse response);
    string FormatSseEvent(string eventName, string data);
    string FormatSseDoneEvent();
}
