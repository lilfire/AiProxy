using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

[TestClass]
public class AigravityProviderTests
{
    [TestMethod]
    public void ParseModelIds_returns_id_part_before_tab()
    {
        var provider = CreateProvider();
        var output = "gemini-3.7-flash-high\tGemini 3.7 Flash (High)\r\ngemini-3.5-flash-low\tGemini 3.5 Flash (Low)";

        var result = provider.ParseModelIds(output);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("gemini-3.7-flash-high", result[0]);
        Assert.AreEqual("gemini-3.5-flash-low", result[1]);
    }

    [TestMethod]
    public void ParseModelIds_uses_whole_line_when_tab_is_missing()
    {
        var provider = CreateProvider();

        var result = provider.ParseModelIds("gpt-oss-120b-medium");

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("gpt-oss-120b-medium", result[0]);
    }

    [TestMethod]
    public void ParseModelIds_skips_blank_lines()
    {
        var provider = CreateProvider();
        var output = "\r\n  \r\ngemini-3.1-pro-high\tGemini 3.1 Pro (High)\r\n";

        var result = provider.ParseModelIds(output);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("gemini-3.1-pro-high", result[0]);
    }

    [TestMethod]
    public void NormalizeModelId_strips_label_from_model_id()
    {
        var provider = CreateProvider();

        var result = provider.NormalizeModelId("gemini-3.5-flash-high\tGemini 3.5 Flash (High)");

        Assert.AreEqual("gemini-3.5-flash-high", result);
    }

    [TestMethod]
    public void NormalizeModelId_returns_model_id_unchanged_when_it_has_no_label()
    {
        var provider = CreateProvider();

        var result = provider.NormalizeModelId("claude-opus-4-6-thinking");

        Assert.AreEqual("claude-opus-4-6-thinking", result);
    }

    [TestMethod]
    public void NormalizeModelId_returns_empty_string_when_model_id_is_blank()
    {
        var provider = CreateProvider();

        var result = provider.NormalizeModelId("   ");

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void BuildArguments_includes_conversation_id_when_provider_session_exists()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildArguments("C:/prompt.txt", "gemini-3.6-flash-high", "conv-123", includeConversationId: true);

        CollectionAssert.Contains(arguments, "--conversation");
        CollectionAssert.Contains(arguments, "conv-123");
        CollectionAssert.Contains(arguments, "-p");
        CollectionAssert.Contains(arguments, "@C:/prompt.txt");
        CollectionAssert.Contains(arguments, "--model");
        CollectionAssert.Contains(arguments, "gemini-3.6-flash-high");
        CollectionAssert.Contains(arguments, "--output-format");
        CollectionAssert.Contains(arguments, "text");
    }

    [TestMethod]
    public void BuildArguments_excludes_conversation_id_when_include_is_false()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildArguments("C:/prompt.txt", "gemini-3.6-flash-high", "conv-123", includeConversationId: false);

        CollectionAssert.DoesNotContain(arguments, "--conversation");
        CollectionAssert.DoesNotContain(arguments, "conv-123");
    }

    [TestMethod]
    public void BuildArguments_excludes_model_when_model_id_is_blank()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildArguments("C:/prompt.txt", string.Empty, "conv-123", includeConversationId: true);

        CollectionAssert.DoesNotContain(arguments, "--model");
    }

    private AigravityProvider CreateProvider()
    {
        var logger = NullLogger<AigravityProvider>.Instance;
        var executablePathResolver = new ExecutablePathResolver();
        var shellRunner = new ShellCommandRunner(executablePathResolver, NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings());
        var sessionStore = new ProviderSessionStore();
        var modelIdCache = new ModelIdCache(new TestRuntimeSettings());
        var promptFileWriter = new PromptFileWriter();

        return new AigravityProvider(logger, shellRunner, sessionStore, modelIdCache, promptFileWriter);
    }

    private static IOptions<AiProxyOptions> CreateOptions()
    {
        return Options.Create(new AiProxyOptions());
    }
}
