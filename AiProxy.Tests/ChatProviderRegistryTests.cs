using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Contracts;
using AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ChatProviderRegistryTests
{
    [TestMethod]
    public async Task Resolve_empty_model_id_throws_unknown_model_exception()
    {
        var registry = CreateRegistry(new[] { new FakeChatProvider("Fake", new[] { "model-1" }, "ok") });

        await Assert.ThrowsExceptionAsync<UnknownModelException>(() => registry.ResolveAsync(string.Empty));
    }

    [TestMethod]
    public async Task Resolve_aigravity_model_matches_available_model()
    {
        var registry = CreateRegistry([]);

        var result = await registry.ResolveAsync("agy-model");

        Assert.AreEqual("Aigravity", result.Provider.Name);
        Assert.AreEqual("agy-model", result.ModelId);
    }

    [TestMethod]
    public async Task Resolve_with_provider_prefix_selects_matching_provider()
    {
        var fakeProvider = new FakeChatProvider("Fake", new[] { "model-1" }, "ok");
        var registry = CreateRegistry(new[] { fakeProvider });

        var result = await registry.ResolveAsync("Fake-model-1");

        Assert.AreEqual("Fake", result.Provider.Name);
        Assert.AreEqual("model-1", result.ModelId);
    }

    [TestMethod]
    public async Task Resolve_without_prefix_matches_available_model()
    {
        var fakeProvider = new FakeChatProvider("Fake", new[] { "model-1" }, "ok");
        var registry = CreateRegistry(new[] { fakeProvider });

        var result = await registry.ResolveAsync("model-1");

        Assert.AreEqual("Fake", result.Provider.Name);
    }

    [TestMethod]
    public async Task Resolve_unknown_model_throws_unknown_model_exception()
    {
        var fakeProvider = new FakeChatProvider("Fake", new[] { "model-1" }, "ok");
        var registry = CreateRegistry(new[] { fakeProvider });

        await Assert.ThrowsExceptionAsync<UnknownModelException>(() => registry.ResolveAsync("unknown-model"));
    }

    [TestMethod]
    public async Task Resolve_grok_prefix_returns_grok_provider()
    {
        var grokProvider = new FakeChatProvider("Grok", new[] { "grok-model" }, "ok");
        var registry = CreateRegistry(new[] { grokProvider });

        var result = await registry.ResolveAsync("grok-vision");

        Assert.AreEqual("Grok", result.Provider.Name);
    }

    [TestMethod]
    public async Task Resolve_claude_prefix_returns_claude_provider()
    {
        var claudeProvider = new FakeChatProvider("Claude", new[] { "claude-model" }, "ok");
        var registry = CreateRegistry(new[] { claudeProvider });

        var result = await registry.ResolveAsync("claude-sonnet");

        Assert.AreEqual("Claude", result.Provider.Name);
    }

    [TestMethod]
    public async Task GetModelsAsync_ignores_duplicate_model_ids()
    {
        var duplicateModelProvider = new FakeChatProvider("Fake", new[] { "agy-model", "agy-model" }, "ok");
        var registry = CreateRegistry(new[] { duplicateModelProvider });

        var models = await registry.GetModelsAsync();

        Assert.AreEqual(2, models.Count);
        Assert.AreEqual(1, models.Count(m => m.Id == "Aigravity-agy-model"));
        Assert.AreEqual(1, models.Count(m => m.Id == "Fake-agy-model"));
    }

    private IChatProviderRegistry CreateRegistry(IEnumerable<IChatProvider> additionalProviders)
    {
        var logger = NullLogger<ChatProviderRegistry>.Instance;
        var modelIdCache = new ModelIdCache(new TestRuntimeSettings());
        var aigravityProvider = new FakeChatProvider("Aigravity", new[] { "agy-model" }, "ok");
        var providers = new List<IChatProvider> { aigravityProvider };
        providers.AddRange(additionalProviders);

        return new ChatProviderRegistry(providers, modelIdCache, new TestRuntimeSettings(), logger);
    }
}
