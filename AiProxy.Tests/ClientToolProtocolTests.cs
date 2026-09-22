using System.Text.Json;
using AiProxy.Contracts;
using AiProxy.Services.Tools;

namespace AiProxy.Tests;

[TestClass]
public class ClientToolProtocolTests
{
    private readonly string _callIdPrefix = "call_test_";

    [TestMethod]
    public void Append_instruction_declares_every_client_tool()
    {
        var prompt = ClientToolProtocol.AppendInstruction(
            "Hei", [Tool("read_file"), Tool("run_command")], null, [], [], new ClientToolInstructionOptions());

        StringAssert.StartsWith(prompt, "Hei");
        StringAssert.Contains(prompt, "read_file");
        StringAssert.Contains(prompt, "run_command");
        StringAssert.Contains(prompt, "<client-tools>");
    }

    [TestMethod]
    public void Append_instruction_inserts_provider_paragraphs_before_the_fence_format()
    {
        var options = new ClientToolInstructionOptions { ExtraParagraphs = ["Bare for denne provideren."] };

        var prompt = ClientToolProtocol.AppendInstruction("Hei", [Tool("read_file")], null, [], [], options);

        var paragraphIndex = prompt.IndexOf("Bare for denne provideren.", StringComparison.Ordinal);
        var fenceIndex = prompt.IndexOf("If a function is needed", StringComparison.Ordinal);
        Assert.IsTrue(paragraphIndex > 0);
        Assert.IsTrue(paragraphIndex < fenceIndex);
    }

    [TestMethod]
    public void Append_instruction_omits_provider_paragraphs_when_none_are_given()
    {
        var prompt = ClientToolProtocol.AppendInstruction("Hei", [Tool("read_file")], null, [], [], new ClientToolInstructionOptions());

        StringAssert.DoesNotMatch(prompt, new System.Text.RegularExpressions.Regex("===FILE"));
        StringAssert.DoesNotMatch(prompt, new System.Text.RegularExpressions.Regex("container.exec"));
    }

    [TestMethod]
    public void Append_instruction_includes_prior_calls_and_results()
    {
        var prompt = ClientToolProtocol.AppendInstruction(
            "Hei",
            [Tool("read_file")],
            null,
            [new OpenAiToolCall("call_1", "read_file", """{"path":"a.txt"}""")],
            [new OpenAiToolResult("call_1", "innhold")],
            new ClientToolInstructionOptions());

        StringAssert.Contains(prompt, "<prior-tool-calls>");
        StringAssert.Contains(prompt, "<tool-responses>");
        StringAssert.Contains(prompt, "innhold");
    }

    [TestMethod]
    public void Parse_returns_call_from_fenced_response()
    {
        var response = Fence("""{"call_id":"call_9","name":"read_file","arguments":{"path":"a.txt"}}""");

        var call = ClientToolProtocol.Parse(response, [Tool("read_file")], _callIdPrefix);

        Assert.IsNotNull(call);
        Assert.AreEqual("call_9", call.CallId);
        Assert.AreEqual("read_file", call.Name);
        Assert.AreEqual("""{"path":"a.txt"}""", call.ArgumentsJson);
    }

    [TestMethod]
    public void Parse_uses_given_prefix_when_the_model_omitted_a_call_id()
    {
        var response = Fence("""{"name":"read_file","arguments":{}}""");

        var call = ClientToolProtocol.Parse(response, [Tool("read_file")], _callIdPrefix);

        Assert.IsNotNull(call);
        StringAssert.StartsWith(call.CallId, _callIdPrefix);
    }

    [TestMethod]
    public void Parse_rejects_a_function_the_client_never_declared()
    {
        var response = Fence("""{"name":"delete_everything","arguments":{}}""");

        var call = ClientToolProtocol.Parse(response, [Tool("read_file")], _callIdPrefix);

        Assert.IsNull(call);
    }

    [TestMethod]
    public void Parse_returns_null_for_ordinary_prose()
    {
        var call = ClientToolProtocol.Parse("Filen inneholder tre linjer.", [Tool("read_file")], _callIdPrefix);

        Assert.IsNull(call);
    }

    [TestMethod]
    public void Is_allowed_by_choice_blocks_every_call_when_choice_is_none()
    {
        var choice = JsonSerializer.SerializeToElement("none");

        Assert.IsFalse(ClientToolProtocol.IsAllowedByChoice(choice, "read_file"));
    }

    [TestMethod]
    public void Is_allowed_by_choice_accepts_only_the_named_function()
    {
        var choice = JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "read_file" } });

        Assert.IsTrue(ClientToolProtocol.IsAllowedByChoice(choice, "read_file"));
        Assert.IsFalse(ClientToolProtocol.IsAllowedByChoice(choice, "run_command"));
    }

    private OpenAiFunctionTool Tool(string name) => new(name, $"Beskrivelse av {name}", null);

    private string Fence(string payload) => "```tool_call" + Environment.NewLine + payload + Environment.NewLine + "```";
}
