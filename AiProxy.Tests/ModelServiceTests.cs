using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Contracts;
using AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ModelServiceTests
{
    [TestMethod]
    public async Task GetModelsAsync_returns_empty_list_when_no_models_are_discovered()
    {
        var settings = new TestRuntimeSettings();
        var registry = new ChatProviderRegistry(
            [new FakeChatProvider("Fake", [], "ok")],
            new ModelIdCache(settings),
            settings,
            NullLogger<ChatProviderRegistry>.Instance);
        var service = new ModelService(registry);

        var models = await service.GetModelsAsync();

        Assert.AreEqual(0, models.Count);
    }
}
