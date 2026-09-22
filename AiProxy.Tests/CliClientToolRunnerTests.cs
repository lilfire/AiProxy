using System.Text.Json;
using AiProxy.Contracts;
using AiProxy.Services.Tools;

namespace AiProxy.Tests;

[TestClass]
public class CliClientToolRunnerTests
{
    private readonly string _providerName = "TestCli";

    [TestMethod]
    public async Task Run_returns_plain_text_when_the_cli_answered_without_a_call()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = CreateRequest();

        var result = await runner.RunAsync(request, "Hei", (_, _) => Task.FromResult("Filen har tre linjer."), CancellationToken.None);

        Assert.IsNull(result.ToolCall);
        Assert.AreEqual("Filen har tre linjer.", result.Text);
    }

    [TestMethod]
    public async Task Run_returns_the_declared_call_and_no_text()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = CreateRequest();

        var result = await runner.RunAsync(
            request,
            "Hei",
            (_, _) => Task.FromResult(Fence("""{"call_id":"call_4","name":"read_file","arguments":{"path":"a.txt"}}""")),
            CancellationToken.None);

        Assert.IsNotNull(result.ToolCall);
        Assert.AreEqual("call_4", result.ToolCall.CallId);
        Assert.AreEqual("read_file", result.ToolCall.Name);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task Run_gives_the_cli_a_prompt_that_declares_the_client_tools()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = CreateRequest();
        var seenPrompt = string.Empty;

        await runner.RunAsync(
            request,
            "Les filen",
            (prompt, _) =>
            {
                seenPrompt = prompt;
                return Task.FromResult("ferdig");
            },
            CancellationToken.None);

        StringAssert.StartsWith(seenPrompt, "Les filen");
        StringAssert.Contains(seenPrompt, "read_file");
        StringAssert.Contains(seenPrompt, "Your own built-in tools are disabled for this turn.");
    }

    [TestMethod]
    public async Task Run_rejects_a_call_that_tool_choice_does_not_allow()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = CreateRequest();
        request.ToolChoice = JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "run_command" } });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runner.RunAsync(
            request,
            "Hei",
            (_, _) => Task.FromResult(Fence("""{"name":"read_file","arguments":{}}""")),
            CancellationToken.None));
    }

    private OpenAiChatRequest CreateRequest()
    {
        var request = new OpenAiChatRequest
        {
            Model = "test-model",
            Messages = [new OpenAiMessage(OpenAiConstants.Roles.User, "Les filen a.txt")]
        };
        request.FunctionTools.Add(new OpenAiFunctionTool("read_file", "Leser en fil", null));
        request.FunctionTools.Add(new OpenAiFunctionTool("run_command", "Kjører en kommando", null));

        return request;
    }

    private string Fence(string payload) => "```tool_call" + Environment.NewLine + payload + Environment.NewLine + "```";
}
