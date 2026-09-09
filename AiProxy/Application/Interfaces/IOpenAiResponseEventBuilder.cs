using AiProxy.Application.Services;

namespace AiProxy.Application.Interfaces;

public interface IOpenAiResponseEventBuilder
{
    string CreateEventJson(string type, string status, string responseId, string model, long createdAt);
    string CreateOutputItemAddedEventJson(string responseId);
    string CreateContentPartAddedEventJson(string responseId);
    string CreateOutputTextDoneEventJson(string responseId, string output);
    string CreateContentPartDoneEventJson(string responseId, string output);
    string CreateOutputItemDoneEventJson(string responseId, string output);
    string CreateCompletedEventJson(string responseId, string model, string output, OpenAiTodoCall? todoCall);
    string CreateFunctionCallItemAddedEventJson(OpenAiTodoCall todoCall);
    string CreateFunctionCallArgumentsDeltaEventJson(OpenAiTodoCall todoCall);
    string CreateFunctionCallArgumentsDoneEventJson(OpenAiTodoCall todoCall);
    string CreateFunctionCallItemDoneEventJson(OpenAiTodoCall todoCall);
}
