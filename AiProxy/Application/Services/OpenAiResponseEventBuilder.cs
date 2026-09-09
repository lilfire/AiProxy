using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class OpenAiResponseEventBuilder : IOpenAiResponseEventBuilder
{
    private readonly string _assistantRole = OpenAiConstants.Roles.Assistant;
    private readonly string _messageType = OpenAiConstants.ResponseObjectTypes.Message;
    private readonly string _outputTextType = OpenAiConstants.ResponseObjectTypes.OutputText;
    private readonly string _functionCallType = OpenAiConstants.ResponseObjectTypes.FunctionCall;
    private readonly int _functionCallOutputIndex = 1;
    private readonly string _outputProperty = "output";

    public string CreateEventJson(string type, string status, string responseId, string model, long createdAt)
    {
        return new JsonObject
        {
            ["type"] = type,
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["object"] = OpenAiConstants.ResponseObject,
                ["created_at"] = createdAt,
                ["model"] = model,
                ["status"] = status,
                ["output"] = new JsonArray()
            }
        }.ToJsonString();
    }

    public string CreateOutputItemAddedEventJson(string responseId)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.OutputItemAdded,
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["id"] = responseId + OpenAiConstants.ResponseItemIdSuffix,
                ["type"] = _messageType,
                ["role"] = _assistantRole,
                ["status"] = OpenAiConstants.ResponseStatuses.InProgress
            }
        }.ToJsonString();
    }

    public string CreateContentPartAddedEventJson(string responseId)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.ContentPartAdded,
            ["item_id"] = responseId + OpenAiConstants.ResponseItemIdSuffix,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = _outputTextType,
                ["text"] = string.Empty
            }
        }.ToJsonString();
    }

    public string CreateOutputTextDoneEventJson(string responseId, string output)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.OutputTextDone,
            ["item_id"] = responseId + OpenAiConstants.ResponseItemIdSuffix,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["text"] = output
        }.ToJsonString();
    }

    public string CreateContentPartDoneEventJson(string responseId, string output)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.ContentPartDone,
            ["item_id"] = responseId + OpenAiConstants.ResponseItemIdSuffix,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = _outputTextType,
                ["text"] = output
            }
        }.ToJsonString();
    }

    public string CreateOutputItemDoneEventJson(string responseId, string output)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.OutputItemDone,
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["id"] = responseId + OpenAiConstants.ResponseItemIdSuffix,
                ["type"] = _messageType,
                ["role"] = _assistantRole,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = _outputTextType,
                        ["text"] = output
                    }
                }
            }
        }.ToJsonString();
    }

    public string CreateCompletedEventJson(string responseId, string model, string output, OpenAiTodoCall? todoCall)
    {
        var response = new OpenAiResponsesResponse(responseId, model, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            Status = OpenAiConstants.ResponseStatuses.Completed,
            Output = new List<OpenAiResponsesOutput>
            {
                new(new List<OpenAiResponsesContent> { new(output) })
            }
        };

        var responseNode = JsonSerializer.SerializeToNode(response);

        AppendFunctionCallItem(responseNode, todoCall);

        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.Completed,
            ["response"] = responseNode
        }.ToJsonString();
    }

    public string CreateFunctionCallItemAddedEventJson(OpenAiTodoCall todoCall)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.OutputItemAdded,
            ["output_index"] = _functionCallOutputIndex,
            ["item"] = CreateFunctionCallItemNode(todoCall, string.Empty, OpenAiConstants.ResponseStatuses.InProgress)
        }.ToJsonString();
    }

    public string CreateFunctionCallArgumentsDeltaEventJson(OpenAiTodoCall todoCall)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.FunctionCallArgumentsDelta,
            ["item_id"] = todoCall.ItemId,
            ["output_index"] = _functionCallOutputIndex,
            ["delta"] = todoCall.ArgumentsJson
        }.ToJsonString();
    }

    public string CreateFunctionCallArgumentsDoneEventJson(OpenAiTodoCall todoCall)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.FunctionCallArgumentsDone,
            ["item_id"] = todoCall.ItemId,
            ["output_index"] = _functionCallOutputIndex,
            ["arguments"] = todoCall.ArgumentsJson
        }.ToJsonString();
    }

    public string CreateFunctionCallItemDoneEventJson(OpenAiTodoCall todoCall)
    {
        return new JsonObject
        {
            ["type"] = OpenAiConstants.ResponseEventTypes.OutputItemDone,
            ["output_index"] = _functionCallOutputIndex,
            ["item"] = CreateFunctionCallItemNode(todoCall, todoCall.ArgumentsJson, OpenAiConstants.ResponseStatuses.Completed)
        }.ToJsonString();
    }

    /// <summary>
    /// AI SDK utleder tool-calls fra at output inneholder et function_call-item, så det må
    /// legges på listen i completed-hendelsen også.
    /// </summary>
    private void AppendFunctionCallItem(JsonNode? responseNode, OpenAiTodoCall? todoCall)
    {
        if (todoCall == null || responseNode == null)
            return;

        if (responseNode[_outputProperty] is not JsonArray outputArray)
            return;

        outputArray.Add(CreateFunctionCallItemNode(todoCall, todoCall.ArgumentsJson, OpenAiConstants.ResponseStatuses.Completed));
    }

    /// <summary>id er item-id-en, call_id er den klienten refererer i verktøyresultatet.</summary>
    private JsonObject CreateFunctionCallItemNode(OpenAiTodoCall todoCall, string arguments, string status)
    {
        return new JsonObject
        {
            ["id"] = todoCall.ItemId,
            ["type"] = _functionCallType,
            ["call_id"] = todoCall.CallId,
            ["name"] = todoCall.Name,
            ["arguments"] = arguments,
            ["status"] = status
        };
    }
}
