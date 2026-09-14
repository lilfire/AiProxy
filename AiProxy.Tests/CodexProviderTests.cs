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
}
