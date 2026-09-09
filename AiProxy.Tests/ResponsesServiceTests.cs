using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

[TestClass]
public class ResponsesServiceTests
{
    private readonly string _proxyCallId = "call_aiproxy_todo_abc123";

    [TestMethod]
    public async Task Execute_streaming_emits_function_call_events_when_todo_snapshot_exists()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(RequestWithTodoTool(), "session-1"));

        StringAssert.Contains(body, "event: response.function_call_arguments.delta");
        StringAssert.Contains(body, "event: response.function_call_arguments.done");
        StringAssert.Contains(body, "call_aiproxy_todo_");
        StringAssert.Contains(body, "\"name\":\"todowrite\"");
    }

    [TestMethod]
    public async Task Execute_streaming_emits_function_call_events_after_the_message_item_is_done()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(RequestWithTodoTool(), "session-1"));

        var textDoneIndex = body.IndexOf("response.output_text.done", StringComparison.Ordinal);
        var functionCallIndex = body.IndexOf("response.function_call_arguments.delta", StringComparison.Ordinal);
        var completedIndex = body.IndexOf("event: response.completed", StringComparison.Ordinal);

        Assert.IsTrue(textDoneIndex > 0);
        Assert.IsTrue(functionCallIndex > textDoneIndex);
        Assert.IsTrue(completedIndex > functionCallIndex);
    }

    [TestMethod]
    public async Task Execute_streaming_includes_function_call_item_in_completed_output()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(RequestWithTodoTool(), "session-1"));
        var completed = ParseLastEventData(body, "response.completed");
        var output = completed.GetProperty("response").GetProperty("output");

        Assert.AreEqual(2, output.GetArrayLength());
        Assert.AreEqual("function_call", output[1].GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task Execute_streaming_omits_function_call_events_when_no_todo_snapshot()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, new TodoSnapshotStore());

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(RequestWithTodoTool(), "session-1"));

        Assert.IsFalse(body.Contains("function_call", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Execute_streaming_omits_function_call_events_when_client_declares_no_todo_tool()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());
        var request = RequestWithTodoTool();
        request.Tools = null;

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(request, "session-1"));

        Assert.IsFalse(body.Contains("function_call", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Execute_streaming_omits_function_call_events_when_bridge_is_disabled()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos(), todoBridgeEnabled: false);

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(RequestWithTodoTool(), "session-1"));

        Assert.IsFalse(body.Contains("function_call", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Execute_short_circuits_proxy_tool_result_without_calling_provider()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());

        await new SseResultReader().ReadAsync(await service.ExecuteAsync(ToolResultRequest(true), "session-1"));

        Assert.IsNull(provider.LastRequest);
    }

    [TestMethod]
    public async Task Execute_short_circuit_returns_a_completed_event_stream_without_function_calls()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(ToolResultRequest(true), "session-1"));

        StringAssert.Contains(body, "event: response.created");
        StringAssert.Contains(body, "event: response.completed");
        StringAssert.Contains(body, "data: [DONE]");
        Assert.IsFalse(body.Contains("function_call", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Execute_short_circuit_returns_json_when_stream_is_false()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());
        var request = ToolResultRequest(true);
        request.Stream = false;

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(request, "session-1"));

        Assert.IsNull(provider.LastRequest);
        StringAssert.Contains(body, "\"object\":\"response\"");
        Assert.IsFalse(body.Contains("event:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Execute_short_circuit_rejects_unknown_model()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, CreateStoreWithTodos());
        var request = ToolResultRequest(true);
        request.Model = "unknown-model";

        await Assert.ThrowsExceptionAsync<UnknownModelException>(() => service.ExecuteAsync(request, "session-1"));
    }

    [TestMethod]
    public async Task Execute_does_not_short_circuit_a_foreign_tool_result()
    {
        var provider = new FakeChatProvider("Fake", ["model-1"], "Ferdig.");
        var service = CreateService(provider, new TodoSnapshotStore());

        await new SseResultReader().ReadAsync(await service.ExecuteAsync(ToolResultRequest(false), "session-1"));

        Assert.IsNotNull(provider.LastRequest);
    }

    [TestMethod]
    public async Task Execute_non_streaming_returns_tool_aware_provider_call_with_original_call_id()
    {
        var provider = new FakeToolAwareChatProvider(["model-1"], new OpenAiToolExecutionResult("", new OpenAiProviderToolCall("call_client_9", "read_file", "{\"path\":\"a.txt\"}")));
        var service = CreateService(provider, CreateStoreWithTodos());
        var request = new OpenAiResponsesRequest
        {
            Model = "model-1",
            Stream = false,
            Input = JsonSerializer.SerializeToElement("Read the file"),
            Tools = [new OpenAiResponsesTool { Type = "function", Name = "read_file", Parameters = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(request, "session-1"));

        StringAssert.Contains(body, "\"call_id\":\"call_client_9\"");
        StringAssert.Contains(body, "\"name\":\"read_file\"");
        Assert.AreEqual(1, provider.LastToolRequest!.FunctionTools.Count);
        Assert.IsFalse(provider.LastToolRequest.Messages.Any(message => message.Content?.Contains("aiproxy-todos", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task Execute_streaming_emits_standard_function_call_events_for_tool_aware_provider()
    {
        var provider = new FakeToolAwareChatProvider(["model-1"], new OpenAiToolExecutionResult("", new OpenAiProviderToolCall("call_client_9", "read_file", "{\"path\":\"a.txt\"}")));
        var service = CreateService(provider, CreateStoreWithTodos());
        var request = new OpenAiResponsesRequest
        {
            Model = "model-1",
            Stream = true,
            Input = JsonSerializer.SerializeToElement("Read the file"),
            Tools = [new OpenAiResponsesTool { Type = "function", Name = "read_file", Parameters = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(request, "session-1"));

        StringAssert.Contains(body, "event: response.function_call_arguments.done");
        StringAssert.Contains(body, "call_client_9");
        Assert.IsFalse(body.Contains("```tool_call", StringComparison.Ordinal));
    }

    private ResponsesService CreateService(IChatProvider provider, ITodoSnapshotStore todoStore, bool todoBridgeEnabled = true)
    {
        var options = Options.Create(new AiProxyOptions { TodoBridgeEnabled = todoBridgeEnabled });
        var settings = new TestRuntimeSettings(options.Value);
        var bridge = new TodoToolBridge(todoStore, new TodoItemMapper(), settings, NullLogger<TodoToolBridge>.Instance);

        return new ResponsesService(
            CreateRegistry(provider),
            new ResponsesRequestMapper(NullLogger<ResponsesRequestMapper>.Instance),
            new ChatRequestFactory(),
            new OpenAiStreamFormatter(),
            new OpenAiResponseEventBuilder(),
            bridge,
            new TodoOutputProtocol(todoStore, NullLogger<TodoOutputProtocol>.Instance),
            new ProviderUsageStore(),
            new InMemorySessionHistoryStore(),
            NullLogger<ResponsesService>.Instance);
    }

    private IChatProviderRegistry CreateRegistry(IChatProvider provider)
    {
        var aigravityProvider = new FakeChatProvider("Aigravity", ["agy-model"], "ok");
        var providers = new List<IChatProvider> { aigravityProvider, provider };

        var settings = new TestRuntimeSettings();
        return new ChatProviderRegistry(providers, new ModelIdCache(settings), settings, NullLogger<ChatProviderRegistry>.Instance);
    }

    private ITodoSnapshotStore CreateStoreWithTodos()
    {
        var store = new TodoSnapshotStore();
        store.Capture(new TodoSnapshot(new List<TodoSnapshotItem> { new("Fiks bug", "in_progress") }));

        return store;
    }

    private OpenAiResponsesRequest RequestWithTodoTool()
    {
        return new OpenAiResponsesRequest
        {
            Model = "model-1",
            Stream = true,
            Input = JsonSerializer.SerializeToElement("Hei"),
            Tools =
            [
                new OpenAiResponsesTool { Type = "function", Name = "todowrite", Parameters = TodoParameters() }
            ]
        };
    }

    private OpenAiResponsesRequest ToolResultRequest(bool useProxyCallId)
    {
        var callId = useProxyCallId ? _proxyCallId : "call_opencode_bash_1";
        var input = new JsonArray(
            new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Hei" },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = "ok" });

        var request = RequestWithTodoTool();
        request.Input = JsonSerializer.Deserialize<JsonElement>(input.ToJsonString());

        return request;
    }

    private JsonElement TodoParameters()
    {
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["todos"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["content"] = new JsonObject { ["type"] = "string" },
                            ["status"] = new JsonObject { ["type"] = "string" },
                            ["priority"] = new JsonObject { ["type"] = "string" }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Deserialize<JsonElement>(parameters.ToJsonString());
    }

    private JsonElement ParseLastEventData(string body, string eventName)
    {
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var data = string.Empty;

        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index].Trim() == $"event: {eventName}")
                data = lines[index + 1].Trim()["data: ".Length..];
        }

        return JsonSerializer.Deserialize<JsonElement>(data);
    }
}
