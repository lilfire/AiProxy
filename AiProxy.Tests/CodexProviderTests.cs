using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class CodexProviderTests
{
    [TestMethod]
    public void BuildArguments_uses_the_provider_permission_policy()
    {
        var provider = new CodexProvider(
            NullLogger<CodexProvider>.Instance,
            new ShellCommandRunner(new ExecutablePathResolver(), NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings()),
            new ProviderSessionStore(),
            new PromptFileWriter(),
            new ImageInputResolver());

        var arguments = provider.BuildArguments(new OpenAiChatRequest { Model = "gpt-5.6" }, "session-1", true, []);

        CollectionAssert.Contains(arguments, "--dangerously-bypass-approvals-and-sandbox");
    }

    [TestMethod]
    public void BuildArguments_uses_a_read_only_sandbox_when_the_client_declared_tools()
    {
        var provider = CreateProvider();
        var request = new OpenAiChatRequest { Model = "gpt-5.6" };
        request.FunctionTools.Add(new OpenAiFunctionTool("read_file", "Leser en fil", null));

        var arguments = provider.BuildArguments(request, "session-1", true, []);

        CollectionAssert.Contains(arguments, "--sandbox");
        CollectionAssert.Contains(arguments, "read-only");
        CollectionAssert.DoesNotContain(arguments, "--dangerously-bypass-approvals-and-sandbox");
    }

    private CodexProvider CreateProvider() =>
        new(
            NullLogger<CodexProvider>.Instance,
            new ShellCommandRunner(new ExecutablePathResolver(), NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings()),
            new ProviderSessionStore(),
            new PromptFileWriter(),
            new ImageInputResolver());
}
