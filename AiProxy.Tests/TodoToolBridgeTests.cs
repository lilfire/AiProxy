using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Application.Services;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

[TestClass]
public class TodoToolBridgeTests
{
    private readonly string _proxyCallId = "call_aiproxy_todo_abc123";

    [TestMethod]
    public void Read_client_schema_when_todowrite_is_not_declared_returns_not_declared()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Tools = [Tool("read", TodoParameters(true, true))] };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsFalse(schema.IsDeclared);
    }

    [TestMethod]
    public void Read_client_schema_when_tools_are_missing_returns_not_declared()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());

        var schema = bridge.ReadClientSchema(new OpenAiResponsesRequest());

        Assert.IsFalse(schema.IsDeclared);
    }

    [TestMethod]
    public void Read_client_schema_matches_tool_name_case_insensitively()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Tools = [Tool("TodoWrite", TodoParameters(true, true))] };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsTrue(schema.IsDeclared);
    }

    [TestMethod]
    public void Read_client_schema_returns_declared_name_exactly_as_client_sent_it()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Tools = [Tool("TodoWrite", TodoParameters(true, true))] };

        var schema = bridge.ReadClientSchema(request);

        Assert.AreEqual("TodoWrite", schema.DeclaredName);
    }

    [TestMethod]
    public void Read_client_schema_when_item_schema_declares_id_returns_supports_id()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Tools = [Tool("todowrite", TodoParameters(true, true))] };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsTrue(schema.SupportsId);
        Assert.IsTrue(schema.SupportsPriority);
    }

    [TestMethod]
    public void Read_client_schema_when_item_schema_omits_priority_returns_no_priority_support()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Tools = [Tool("todowrite", TodoParameters(false, false))] };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsFalse(schema.SupportsId);
        Assert.IsFalse(schema.SupportsPriority);
    }

    [TestMethod]
    public void Read_client_schema_when_parameters_are_missing_falls_back_to_priority_without_id()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Tools = [Tool("todowrite", null)] };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsTrue(schema.IsDeclared);
        Assert.IsFalse(schema.SupportsId);
        Assert.IsTrue(schema.SupportsPriority);
    }

    [TestMethod]
    public void Read_client_schema_when_tool_choice_is_none_returns_not_declared()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest
        {
            Tools = [Tool("todowrite", TodoParameters(true, true))],
            ToolChoice = JsonSerializer.SerializeToElement("none")
        };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsFalse(schema.IsDeclared);
    }

    [TestMethod]
    public void Read_client_schema_when_tool_choice_forces_another_tool_returns_not_declared()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest
        {
            Tools = [Tool("todowrite", TodoParameters(true, true))],
            ToolChoice = JsonSerializer.SerializeToElement(new { type = "function", name = "bash" })
        };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsFalse(schema.IsDeclared);
    }

    [TestMethod]
    public void Read_client_schema_when_bridge_is_disabled_returns_not_declared()
    {
        var bridge = CreateBridge(new TodoSnapshotStore(), todoBridgeEnabled: false);
        var request = new OpenAiResponsesRequest { Tools = [Tool("todowrite", TodoParameters(true, true))] };

        var schema = bridge.ReadClientSchema(request);

        Assert.IsFalse(schema.IsDeclared);
    }

    [TestMethod]
    public void Try_create_todo_call_when_snapshot_is_missing_returns_null()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());

        var call = bridge.TryCreateTodoCall(new TodoToolSchema(true, "todowrite", true, true));

        Assert.IsNull(call);
    }

    [TestMethod]
    public void Try_create_todo_call_when_schema_is_not_declared_returns_null()
    {
        var bridge = CreateBridge(CreateStoreWithTodos());

        var call = bridge.TryCreateTodoCall(new TodoToolSchema(false, string.Empty, false, false));

        Assert.IsNull(call);
    }

    [TestMethod]
    public void Try_create_todo_call_uses_proxy_prefix_in_call_id()
    {
        var bridge = CreateBridge(CreateStoreWithTodos());

        var call = bridge.TryCreateTodoCall(new TodoToolSchema(true, "todowrite", true, true));

        Assert.IsNotNull(call);
        Assert.IsTrue(call.CallId.StartsWith("call_aiproxy_todo_", StringComparison.Ordinal));
        Assert.IsTrue(call.ItemId.StartsWith("fc_aiproxy_todo_", StringComparison.Ordinal));
        Assert.AreNotEqual(call.CallId, call.ItemId);
    }

    [TestMethod]
    public void Try_create_todo_call_uses_declared_tool_name()
    {
        var bridge = CreateBridge(CreateStoreWithTodos());

        var call = bridge.TryCreateTodoCall(new TodoToolSchema(true, "TodoWrite", true, true));

        Assert.IsNotNull(call);
        Assert.AreEqual("TodoWrite", call.Name);
    }

    [TestMethod]
    public void Try_create_todo_call_serializes_todos_as_json_string_arguments()
    {
        var bridge = CreateBridge(CreateStoreWithTodos());

        var call = bridge.TryCreateTodoCall(new TodoToolSchema(true, "todowrite", true, true));

        Assert.IsNotNull(call);

        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var todos = document.RootElement.GetProperty("todos");

        Assert.AreEqual(2, todos.GetArrayLength());
        Assert.AreEqual("Fiks bug", todos[0].GetProperty("content").GetString());
        Assert.AreEqual("in_progress", todos[0].GetProperty("status").GetString());
        Assert.AreEqual("todo-1", todos[0].GetProperty("id").GetString());
        Assert.AreEqual("medium", todos[0].GetProperty("priority").GetString());
    }

    [TestMethod]
    public void Try_create_todo_call_omits_unsupported_fields_from_arguments()
    {
        var bridge = CreateBridge(CreateStoreWithTodos());

        var call = bridge.TryCreateTodoCall(new TodoToolSchema(true, "todowrite", SupportsId: false, SupportsPriority: false));

        Assert.IsNotNull(call);

        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var todo = document.RootElement.GetProperty("todos")[0];

        Assert.IsFalse(todo.TryGetProperty("id", out _));
        Assert.IsFalse(todo.TryGetProperty("priority", out _));
    }

    [TestMethod]
    public void Is_proxy_tool_result_follow_up_when_last_item_is_our_function_call_output_returns_true()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = RequestWithInput(UserMessage(), FunctionCallOutput(_proxyCallId));

        Assert.IsTrue(bridge.IsProxyToolResultFollowUp(request));
    }

    [TestMethod]
    public void Is_proxy_tool_result_follow_up_when_call_id_is_foreign_returns_false()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = RequestWithInput(UserMessage(), FunctionCallOutput("call_opencode_bash_1"));

        Assert.IsFalse(bridge.IsProxyToolResultFollowUp(request));
    }

    [TestMethod]
    public void Is_proxy_tool_result_follow_up_when_trailing_outputs_are_mixed_returns_false()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = RequestWithInput(UserMessage(), FunctionCallOutput(_proxyCallId), FunctionCallOutput("call_opencode_bash_1"));

        Assert.IsFalse(bridge.IsProxyToolResultFollowUp(request));
    }

    [TestMethod]
    public void Is_proxy_tool_result_follow_up_when_last_item_is_a_user_message_returns_false()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = RequestWithInput(FunctionCallOutput(_proxyCallId), UserMessage());

        Assert.IsFalse(bridge.IsProxyToolResultFollowUp(request));
    }

    [TestMethod]
    public void Is_proxy_tool_result_follow_up_when_input_is_a_plain_string_returns_false()
    {
        var bridge = CreateBridge(new TodoSnapshotStore());
        var request = new OpenAiResponsesRequest { Input = JsonSerializer.SerializeToElement("Hei") };

        Assert.IsFalse(bridge.IsProxyToolResultFollowUp(request));
    }

    [TestMethod]
    public void Is_proxy_tool_result_follow_up_when_bridge_is_disabled_still_returns_true()
    {
        var bridge = CreateBridge(new TodoSnapshotStore(), todoBridgeEnabled: false);
        var request = RequestWithInput(UserMessage(), FunctionCallOutput(_proxyCallId));

        Assert.IsTrue(bridge.IsProxyToolResultFollowUp(request));
    }

    private TodoToolBridge CreateBridge(ITodoSnapshotStore store, bool todoBridgeEnabled = true)
    {
        var options = Options.Create(new AiProxyOptions { TodoBridgeEnabled = todoBridgeEnabled });

        return new TodoToolBridge(store, new TodoItemMapper(), new TestRuntimeSettings(options.Value), NullLogger<TodoToolBridge>.Instance);
    }

    private ITodoSnapshotStore CreateStoreWithTodos()
    {
        var store = new TodoSnapshotStore();
        store.Capture(new TodoSnapshot(new List<TodoSnapshotItem>
        {
            new("Fiks bug", "in_progress"),
            new("Skriv test", "pending")
        }));

        return store;
    }

    private OpenAiResponsesTool Tool(string name, JsonElement? parameters)
    {
        return new OpenAiResponsesTool
        {
            Type = "function",
            Name = name,
            Parameters = parameters
        };
    }

    private JsonElement TodoParameters(bool withId, bool withPriority)
    {
        var itemProperties = new JsonObject
        {
            ["content"] = new JsonObject { ["type"] = "string" },
            ["status"] = new JsonObject { ["type"] = "string" }
        };

        if (withId)
            itemProperties["id"] = new JsonObject { ["type"] = "string" };

        if (withPriority)
            itemProperties["priority"] = new JsonObject { ["type"] = "string" };

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
                        ["properties"] = itemProperties
                    }
                }
            }
        };

        return JsonSerializer.Deserialize<JsonElement>(parameters.ToJsonString());
    }

    private OpenAiResponsesRequest RequestWithInput(params JsonObject[] items)
    {
        var array = new JsonArray(items.Cast<JsonNode>().ToArray());

        return new OpenAiResponsesRequest
        {
            Input = JsonSerializer.Deserialize<JsonElement>(array.ToJsonString())
        };
    }

    private JsonObject FunctionCallOutput(string callId)
    {
        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = "ok"
        };
    }

    private JsonObject UserMessage()
    {
        return new JsonObject
        {
            ["type"] = "message",
            ["role"] = "user",
            ["content"] = "Hei"
        };
    }
}
