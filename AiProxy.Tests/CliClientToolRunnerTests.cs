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
    public async Task Run_gives_the_cli_every_declared_metamcp_tool()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest { Model = "test-model" };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_pull_request", "Gets pull request metadata", null));
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        var seenPrompt = string.Empty;

        await runner.RunAsync(
            request,
            "Review PR 2475",
            (prompt, _) =>
            {
                seenPrompt = prompt;
                return Task.FromResult("ferdig");
            },
            CancellationToken.None);

        StringAssert.Contains(seenPrompt, "metamcp_azure-devops__repo_pull_request");
        StringAssert.Contains(seenPrompt, "metamcp_azure-devops__repo_file");
        StringAssert.Contains(seenPrompt, "active client-proxied MCP functions");
        StringAssert.Contains(seenPrompt, "Never ask the user to install, enable, or reconnect an MCP server");
        StringAssert.Contains(seenPrompt, "friendly label such as `code-review`");
        StringAssert.Contains(seenPrompt, "an Azure DevOps pull-request review should begin");
        StringAssert.Contains(seenPrompt, "Pull-request metadata and a changed-file list are not sufficient");
        StringAssert.Contains(seenPrompt, "Do not say that repository file contents, a diff, or a repository MCP is unavailable");
    }

    [TestMethod]
    public async Task Run_retries_an_incomplete_azure_devops_review_as_a_repository_file_call()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest { Model = "test-model" };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_pull_request", "Gets pull request metadata", null));
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        var prompts = new List<string>();

        var result = await runner.RunAsync(
            request,
            "Review PR 2475",
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult(prompts.Count == 1
                    ? "I need the changed files' contents at both revisions to complete the review."
                    : Fence("""{"name":"metamcp_azure-devops__repo_file","arguments":{"path":"src/App.vue"}}"""));
            },
            CancellationToken.None);

        Assert.AreEqual(2, prompts.Count);
        Assert.IsNotNull(result.ToolCall);
        Assert.AreEqual("metamcp_azure-devops__repo_file", result.ToolCall.Name);
        StringAssert.Contains(prompts[1], "<tool-call-repair>");
    }

    [TestMethod]
    public async Task Run_retries_a_plain_text_follow_up_after_a_pull_request_result()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest
        {
            Model = "test-model",
            Messages = [new OpenAiMessage("user", "using the mcp code-review PR 2475")],
            HasToolResultsSinceLastUserMessage = true
        };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        request.PreviousToolCalls.Add(new OpenAiToolCall("call_pr", "metamcp_azure-devops__repo_pull_request", "{}"));
        request.ToolResults.Add(new OpenAiToolResult("call_pr", "changed files"));
        var prompts = new List<string>();

        var result = await runner.RunAsync(
            request,
            "Continue the code review",
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult(prompts.Count == 1
                    ? "Retrieving PR 2475 metadata and changed files."
                    : Fence("""{"name":"metamcp_azure-devops__repo_file","arguments":{"path":"src/App.vue"}}"""));
            },
            CancellationToken.None);

        Assert.AreEqual(2, prompts.Count);
        Assert.IsNotNull(result.ToolCall);
        Assert.AreEqual("metamcp_azure-devops__repo_file", result.ToolCall.Name);
    }

    [TestMethod]
    public async Task Run_requests_another_file_when_a_review_still_reports_missing_files()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest
        {
            Model = "test-model",
            Messages = [new OpenAiMessage("user", "code-review PR 2475")]
        };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        request.PreviousToolCalls.Add(new OpenAiToolCall("call_file", "metamcp_azure-devops__repo_file", "{}"));
        request.ToolResults.Add(new OpenAiToolResult("call_file", "file content"));
        var prompts = new List<string>();

        var result = await runner.RunAsync(request, "Review PR 2475", (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompts.Count == 1
                ? "The remaining four changed files could not be fetched, so the review is incomplete."
                : Fence("""{"name":"metamcp_azure-devops__repo_file","arguments":{"path":"src/App.vue"}}"""));
        }, CancellationToken.None);

        Assert.AreEqual(2, prompts.Count);
        StringAssert.Contains(prompts[1], "<tool-call-repair>");
        Assert.AreEqual("metamcp_azure-devops__repo_file", result.ToolCall?.Name);
    }

    [TestMethod]
    public async Task Run_requests_another_file_when_the_review_needs_remaining_mcp_file_responses()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest { Model = "test-model" };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        var prompts = new List<string>();

        var result = await runner.RunAsync(request, "Review PR 2475", (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompts.Count == 1
                ? "I need the remaining MCP file responses to complete the review."
                : Fence("""{"name":"metamcp_azure-devops__repo_file","arguments":{"path":"src/App.vue"}}"""));
        }, CancellationToken.None);

        Assert.AreEqual(2, prompts.Count);
        Assert.AreEqual("metamcp_azure-devops__repo_file", result.ToolCall?.Name);
    }

    [TestMethod]
    public async Task Run_fetches_each_changed_pr_file_at_both_revisions_before_asking_the_model()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest
        {
            Model = "test-model",
            Messages = [new OpenAiMessage("user", "code-review PR 2475")]
        };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        request.PreviousToolCalls.Add(new OpenAiToolCall("call_pr", "metamcp_azure-devops__repo_pull_request", "{}"));
        request.ToolResults.Add(new OpenAiToolResult("call_pr", """
            <<untrusted>>
            {"repository":{"name":"Product.Monitor.Web","project":{"name":"Product.Monitor"}},"lastMergeSourceCommit":{"commitId":"source"},"lastMergeTargetCommit":{"commitId":"target"},"changedFilesSummary":{"changeEntries":[{"item":{"path":"/src/A.vue"}},{"item":{"path":"/src/B.vue"}}]}}
            <</untrusted>>
            """));
        var modelRuns = 0;

        foreach (var (path, version) in new[]
                 {
                     ("/src/A.vue", "source"), ("/src/A.vue", "target"),
                     ("/src/B.vue", "source"), ("/src/B.vue", "target")
                 })
        {
            var result = await runner.RunAsync(request, "Review", (_, _) =>
            {
                modelRuns++;
                return Task.FromResult("done");
            }, CancellationToken.None);

            Assert.IsNotNull(result.ToolCall);
            using var arguments = JsonDocument.Parse(result.ToolCall.ArgumentsJson);
            Assert.AreEqual(path, arguments.RootElement.GetProperty("path").GetString());
            Assert.AreEqual(version, arguments.RootElement.GetProperty("version").GetString());
            Assert.AreEqual("Product.Monitor", arguments.RootElement.GetProperty("project").GetString());
            request.PreviousToolCalls.Add(new OpenAiToolCall(result.ToolCall.CallId, result.ToolCall.Name, result.ToolCall.ArgumentsJson));
            request.ToolResults.Add(new OpenAiToolResult(result.ToolCall.CallId, "file content"));
        }

        var final = await runner.RunAsync(request, "Review", (_, _) =>
        {
            modelRuns++;
            return Task.FromResult("review complete");
        }, CancellationToken.None);
        Assert.AreEqual(1, modelRuns);
        Assert.AreEqual("review complete", final.Text);
    }

    [TestMethod]
    public async Task Run_starts_an_explicit_azure_devops_pr_review_with_the_declared_pr_tool()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest
        {
            Model = "test-model",
            Messages = [new OpenAiMessage("user", "using the mcp code-review the pr 2475 in project Product.Monitor repo Product.Monitor.Web")]
        };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_pull_request", "Gets pull request metadata", null));
        var modelRuns = 0;

        var result = await runner.RunAsync(request, "Review", (_, _) =>
        {
            modelRuns++;
            return Task.FromResult("MCP unavailable");
        }, CancellationToken.None);

        Assert.AreEqual(0, modelRuns);
        Assert.IsNotNull(result.ToolCall);
        Assert.AreEqual("metamcp_azure-devops__repo_pull_request", result.ToolCall.Name);
        using var arguments = JsonDocument.Parse(result.ToolCall.ArgumentsJson);
        Assert.AreEqual(2475, arguments.RootElement.GetProperty("pullRequestId").GetInt32());
        Assert.AreEqual("Product.Monitor", arguments.RootElement.GetProperty("project").GetString());
        Assert.AreEqual("Product.Monitor.Web", arguments.RootElement.GetProperty("repositoryId").GetString());
    }

    [DataTestMethod]
    [DataRow("Unable to continue the MCP review because the declared client tools are not callable in this environment.")]
    [DataRow("I’m unable to continue the review because this turn’s tool protocol requires a tool call, but no callable client tool interface is available to me in the current session.")]
    [DataRow("I’m unable to continue the required MCP tool loop in this environment.")]
    [DataRow("I can’t complete the PR review: the Azure DevOps MCP calls shown in the prompt aren’t available in this session, and I only have one source-side file with no target revision for comparison. Re-enable the Azure DevOps repository MCP and I’ll review all six changed files.")]
    [DataRow("I’m unable to continue the review because the required client tool loop is not available in this session.")]
    [DataRow("I’m unable to continue the requested MCP review from this interface because the client-proxied tools are not available in the active session.")]
    public async Task Run_repairs_a_false_claim_that_completed_mcp_tools_are_not_callable(string falseAvailabilityClaim)
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest { Model = "test-model" };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        request.PreviousToolCalls.Add(new OpenAiToolCall("call_file", "metamcp_azure-devops__repo_file", "{}"));
        request.ToolResults.Add(new OpenAiToolResult("call_file", "<template>Changed file</template>"));
        var prompts = new List<string>();

        var result = await runner.RunAsync(
            request,
            "Review the pull request",
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult(prompts.Count == 1
                    ? falseAvailabilityClaim
                    : "The changed file contains a template.");
            },
            CancellationToken.None);

        Assert.AreEqual(2, prompts.Count);
        StringAssert.Contains(prompts[1], "<tool-result-repair>");
        StringAssert.Contains(prompts[1], "Changed file");
        Assert.AreEqual("The changed file contains a template.", result.Text);
    }

    [TestMethod]
    public async Task Run_retries_a_repeated_false_mcp_availability_claim_as_a_file_call()
    {
        var runner = new CliClientToolRunner(_providerName);
        var request = new OpenAiChatRequest
        {
            Model = "test-model",
            Messages = [new OpenAiMessage("user", "code-review PR 2475")]
        };
        request.FunctionTools.Add(new OpenAiFunctionTool("metamcp_azure-devops__repo_file", "Reads repository files", null));
        request.PreviousToolCalls.Add(new OpenAiToolCall("call_file", "metamcp_azure-devops__repo_file", "{}"));
        request.ToolResults.Add(new OpenAiToolResult("call_file", "file content"));
        var prompts = new List<string>();

        var result = await runner.RunAsync(request, "Review PR 2475", (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompts.Count < 3
                ? "I’m unable to continue the required MCP tool loop in this environment."
                : Fence("""{"name":"metamcp_azure-devops__repo_file","arguments":{"path":"src/App.vue"}}"""));
        }, CancellationToken.None);

        Assert.AreEqual(3, prompts.Count);
        StringAssert.Contains(prompts[1], "Read the next changed file or its target revision");
        StringAssert.Contains(prompts[2], "This is the final retry");
        Assert.AreEqual("metamcp_azure-devops__repo_file", result.ToolCall?.Name);
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
