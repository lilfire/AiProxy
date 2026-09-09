using System.Text.Json;
using AiProxy.Application.Services;

namespace AiProxy.Tests;

[TestClass]
public class OpenAiResponseEventBuilderTests
{
    private readonly string _todoArguments = """{"todos":[{"content":"Fiks bug","status":"in_progress"}]}""";

    [TestMethod]
    public void Create_completed_event_json_uses_stream_response_id_and_completed_status()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateCompletedEventJson("resp_stream", "claude-opus", "Svar", null);
        using var document = JsonDocument.Parse(result);
        var response = document.RootElement.GetProperty("response");

        Assert.AreEqual("response.completed", document.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("resp_stream", response.GetProperty("id").GetString());
        Assert.AreEqual("completed", response.GetProperty("status").GetString());
        Assert.AreEqual(0, response.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.AreEqual(0, response.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [TestMethod]
    public void Create_output_text_done_event_json_carries_full_text_and_part_position()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateOutputTextDoneEventJson("resp_stream", "eple banan");
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;

        Assert.AreEqual("response.output_text.done", root.GetProperty("type").GetString());
        Assert.AreEqual("resp_stream_0", root.GetProperty("item_id").GetString());
        Assert.AreEqual(0, root.GetProperty("output_index").GetInt32());
        Assert.AreEqual(0, root.GetProperty("content_index").GetInt32());
        Assert.AreEqual("eple banan", root.GetProperty("text").GetString());
    }

    [TestMethod]
    public void Create_event_json_includes_required_response_metadata()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateEventJson("response.created", "in_progress", "resp_stream", "claude-opus", 123);
        using var document = JsonDocument.Parse(result);
        var response = document.RootElement.GetProperty("response");

        Assert.AreEqual("resp_stream", response.GetProperty("id").GetString());
        Assert.AreEqual("claude-opus", response.GetProperty("model").GetString());
        Assert.AreEqual(123, response.GetProperty("created_at").GetInt64());
        Assert.AreEqual(JsonValueKind.Array, response.GetProperty("output").ValueKind);
        Assert.AreEqual(0, response.GetProperty("output").GetArrayLength());
    }

    [TestMethod]
    public void Create_function_call_item_added_event_json_starts_with_empty_arguments()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateFunctionCallItemAddedEventJson(TodoCall());
        using var document = JsonDocument.Parse(result);
        var item = document.RootElement.GetProperty("item");

        Assert.AreEqual("response.output_item.added", document.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("output_index").GetInt32());
        Assert.AreEqual("function_call", item.GetProperty("type").GetString());
        Assert.AreEqual("fc_aiproxy_todo_1", item.GetProperty("id").GetString());
        Assert.AreEqual("call_aiproxy_todo_1", item.GetProperty("call_id").GetString());
        Assert.AreEqual("todowrite", item.GetProperty("name").GetString());
        Assert.AreEqual(string.Empty, item.GetProperty("arguments").GetString());
    }

    [TestMethod]
    public void Create_function_call_arguments_delta_event_json_carries_arguments_as_string()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateFunctionCallArgumentsDeltaEventJson(TodoCall());
        using var document = JsonDocument.Parse(result);

        Assert.AreEqual("response.function_call_arguments.delta", document.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("fc_aiproxy_todo_1", document.RootElement.GetProperty("item_id").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("output_index").GetInt32());
        Assert.AreEqual(_todoArguments, document.RootElement.GetProperty("delta").GetString());
    }

    [TestMethod]
    public void Create_function_call_arguments_done_event_json_carries_full_arguments()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateFunctionCallArgumentsDoneEventJson(TodoCall());
        using var document = JsonDocument.Parse(result);

        Assert.AreEqual("response.function_call_arguments.done", document.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(_todoArguments, document.RootElement.GetProperty("arguments").GetString());
    }

    [TestMethod]
    public void Create_function_call_item_done_event_json_is_completed_with_arguments()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateFunctionCallItemDoneEventJson(TodoCall());
        using var document = JsonDocument.Parse(result);
        var item = document.RootElement.GetProperty("item");

        Assert.AreEqual("response.output_item.done", document.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(1, document.RootElement.GetProperty("output_index").GetInt32());
        Assert.AreEqual("completed", item.GetProperty("status").GetString());
        Assert.AreEqual(_todoArguments, item.GetProperty("arguments").GetString());
    }

    [TestMethod]
    public void Create_completed_event_json_appends_function_call_item_to_output()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateCompletedEventJson("resp_stream", "claude-opus", "Svar", TodoCall());
        using var document = JsonDocument.Parse(result);
        var output = document.RootElement.GetProperty("response").GetProperty("output");

        Assert.AreEqual(2, output.GetArrayLength());
        Assert.AreEqual("message", output[0].GetProperty("type").GetString());
        Assert.AreEqual("function_call", output[1].GetProperty("type").GetString());
        Assert.AreEqual("call_aiproxy_todo_1", output[1].GetProperty("call_id").GetString());
    }

    [TestMethod]
    public void Create_completed_event_json_without_todo_call_keeps_only_the_message_item()
    {
        var builder = new OpenAiResponseEventBuilder();

        var result = builder.CreateCompletedEventJson("resp_stream", "claude-opus", "Svar", null);
        using var document = JsonDocument.Parse(result);
        var output = document.RootElement.GetProperty("response").GetProperty("output");

        Assert.AreEqual(1, output.GetArrayLength());
    }

    private OpenAiTodoCall TodoCall()
    {
        return new OpenAiTodoCall("fc_aiproxy_todo_1", "call_aiproxy_todo_1", "todowrite", _todoArguments);
    }
}
