using System.Text.Json;
using AiProxy.Contracts;
using AiProxy.Services.Providers;

namespace AiProxy.Tests;

[TestClass]
public sealed class M365ToolProtocolTests
{
    private static readonly OpenAiFunctionTool ReadFile = new(
        "read_file", "Reads one workspace file", JsonSerializer.SerializeToElement(new { type = "object" }));

    [TestMethod]
    public void Try_parse_accepts_one_declared_fenced_call_and_preserves_call_id()
    {
        var response = """
            ```tool_call
            {"call_id":"call_client_42","name":"read_file","arguments":{"path":"README.md"}}
            ```
            """;

        var parsed = M365ToolProtocol.TryParse(response, [ReadFile], out var call);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(call);
        Assert.AreEqual("call_client_42", call.CallId);
        Assert.AreEqual("read_file", call.Name);
        Assert.AreEqual("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [TestMethod]
    public void Try_parse_accepts_common_json_fence_and_generates_call_id_when_m365_omits_one()
    {
        var response = """
            ```json
            {"tool":"read_file","arguments":"{\"path\":\"README.md\"}"}
            ```
            """;

        var parsed = M365ToolProtocol.TryParse(response, [ReadFile], out var call);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(call);
        Assert.AreEqual("read_file", call.Name);
        Assert.IsTrue(call.CallId.StartsWith("call_m365_", StringComparison.Ordinal));
        Assert.AreEqual("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [TestMethod]
    public void Try_parse_accepts_unfenced_native_marker_with_a_redundant_brace_pair()
    {
        var response = """
            tool_call
            {{"call_id":"call_readme_project_files","name":"read_file","arguments":{"path":"README.md"}}}
            """;

        var parsed = M365ToolProtocol.TryParse(response, [ReadFile], out var call);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(call);
        Assert.AreEqual("call_readme_project_files", call.CallId);
        Assert.AreEqual("read_file", call.Name);
        Assert.AreEqual("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [TestMethod]
    public void Try_parse_accepts_one_final_native_marker_after_a_status_message()
    {
        var response = """
            I will inspect the project first.
            tool_call
            {"call_id":"call_project_files","name":"read_file","arguments":{"path":"README.md"}}
            """;

        var parsed = M365ToolProtocol.TryParse(response, [ReadFile], out var call);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(call);
        Assert.AreEqual("call_project_files", call.CallId);
    }

    [TestMethod]
    public void Tool_marker_with_invalid_json_is_recognized_as_a_tool_attempt()
    {
        Assert.IsTrue(M365ToolProtocol.IsToolFence("tool_call\n{{not valid json}}"));
    }

    [TestMethod]
    public void Requires_declared_write_call_for_a_user_readme_request_with_a_write_tool()
    {
        var writeTool = new OpenAiFunctionTool("apply_patch", "Applies changes to workspace files", null, type: "custom");
        var messages = new[] { new OpenAiMessage("user", "Les prosjektfilene og skriv README.MD.") };

        Assert.IsTrue(M365ToolProtocol.RequiresDeclaredWriteCall(messages, [writeTool]));
    }

    [TestMethod]
    public void Does_not_require_write_call_for_an_inspection_request()
    {
        var writeTool = new OpenAiFunctionTool("apply_patch", "Applies changes to workspace files", null, type: "custom");
        var messages = new[] { new OpenAiMessage("user", "Les README.MD og oppsummer innholdet.") };

        Assert.IsFalse(M365ToolProtocol.RequiresDeclaredWriteCall(messages, [writeTool]));
    }

    [TestMethod]
    public void Try_parse_preserves_raw_custom_tool_input()
    {
        var customTool = new OpenAiFunctionTool("apply_patch", "Applies a patch", null, type: "custom");
        var response = """
            tool_call
            {"call_id":"call_patch_readme","name":"apply_patch","arguments":"*** Begin Patch\n*** Update File: README.MD\n*** End Patch"}
            """;

        var parsed = M365ToolProtocol.TryParse(response, [customTool], out var call);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(call);
        Assert.AreEqual("custom", call.Type);
        Assert.AreEqual("*** Begin Patch\n*** Update File: README.MD\n*** End Patch", call.ArgumentsJson);
    }

    [DataTestMethod]
    [DataRow("unknown")]
    [DataRow("read_file")]
    public void Try_parse_rejects_undeclared_or_proxy_owned_call_ids(string name)
    {
        var callId = name == "read_file" ? "call_aiproxy_todo_not_client" : "call_client_42";
        var response = $"```tool_call\n{{\"call_id\":\"{callId}\",\"name\":\"{name}\",\"arguments\":{{}}}}\n```";

        Assert.IsFalse(M365ToolProtocol.TryParse(response, [ReadFile], out _));
    }

    [TestMethod]
    public void Tool_choice_none_rejects_a_call_but_a_normal_code_fence_is_not_a_tool_fence()
    {
        var choice = JsonSerializer.SerializeToElement("none");

        Assert.IsFalse(M365ToolProtocol.IsAllowedByChoice(choice, "read_file"));
        Assert.IsFalse(M365ToolProtocol.IsToolFence("```csharp\nConsole.WriteLine(\"ok\");\n```"));
    }

    [TestMethod]
    public void Append_instruction_includes_schemas_and_tool_results_as_separate_context()
    {
        var prompt = M365ToolProtocol.AppendInstruction("USER:\nRead it", [ReadFile], null,
            [new OpenAiToolCall("call_client_1", "read_file", "{\"path\":\"a.txt\"}")],
            [new OpenAiToolResult("call_client_1", "file contents")]);

        StringAssert.Contains(prompt, "\"read_file\"");
        StringAssert.Contains(prompt, "<tool-responses>");
        StringAssert.Contains(prompt, "file contents");
        StringAssert.Contains(prompt, "existing workspace layout as fixed");
        StringAssert.Contains(prompt, "prioritize completing that file");
        StringAssert.Contains(prompt, "workspace snapshot is read-only evidence");
    }
}
