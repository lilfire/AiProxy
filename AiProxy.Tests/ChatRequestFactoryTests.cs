using AiProxy.Application.Services;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class ChatRequestFactoryTests
{
    [TestMethod]
    public void CreateProviderRequest_maps_model_and_preserves_other_fields()
    {
        var factory = new ChatRequestFactory();
        var original = new OpenAiChatRequest
        {
            Model = "display-model",
            Messages = new List<OpenAiMessage> { new("user", "Hi") },
            Stream = true,
            Temperature = 0.7,
            MaxTokens = 100
        };
        var resolution = new ModelResolution
        {
            Provider = null!,
            ModelId = "provider-model",
            DisplayModelId = "display-model"
        };

        var result = factory.CreateProviderRequest(original, resolution);

        Assert.AreEqual("provider-model", result.Model);
        Assert.AreEqual(original.Messages, result.Messages);
        Assert.AreEqual(original.Stream, result.Stream);
        Assert.AreEqual(original.Temperature, result.Temperature);
        Assert.AreEqual(original.MaxTokens, result.MaxTokens);
    }
}
