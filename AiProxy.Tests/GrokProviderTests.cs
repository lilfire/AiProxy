using AiProxy.Application.Services;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class GrokProviderTests
{
    private readonly string _promptFilePath = "C:/prompt.txt";

    [TestMethod]
    public void BuildArguments_keeps_built_in_tools_when_the_client_declared_none()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildArguments(new OpenAiChatRequest { Model = "grok-4" }, _promptFilePath, "sess-1", true);

        CollectionAssert.Contains(arguments, "--prompt-file");
        CollectionAssert.DoesNotContain(arguments, "--tools");
    }

    [TestMethod]
    public void BuildArguments_disables_built_in_tools_when_the_client_declared_tools()
    {
        var provider = CreateProvider();
        var request = new OpenAiChatRequest { Model = "grok-4" };
        request.FunctionTools.Add(new OpenAiFunctionTool("read_file", "Leser en fil", null));

        var arguments = provider.BuildArguments(request, _promptFilePath, "sess-1", true);

        CollectionAssert.Contains(arguments, "--tools");
        Assert.AreEqual(string.Empty, arguments[arguments.IndexOf("--tools") + 1]);
    }

    private GrokProvider CreateProvider() =>
        new(
            NullLogger<GrokProvider>.Instance,
            new ShellCommandRunner(new ExecutablePathResolver(), NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings()),
            new ProviderSessionStore(),
            new ModelIdCache(new TestRuntimeSettings()),
            new PromptFileWriter(),
            new ImageInputResolver());
}
