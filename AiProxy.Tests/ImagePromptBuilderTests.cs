using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class ImagePromptBuilderTests
{
    [TestMethod]
    public void Build_keeps_each_image_with_its_message()
    {
        var messages = new List<OpenAiMessage>
        {
            new("user", "Første") { Images = [new OpenAiImageInput("data:image/png;base64,AA==")] },
            new("assistant", "Andre") { Images = [new OpenAiImageInput("data:image/jpeg;base64,AA==")] }
        };

        var prompt = ImagePromptBuilder.Build(messages, ["C:\\Temp\\one.png", "C:\\Temp\\two.jpg"]);

        StringAssert.Contains(prompt, $"user: Første{Environment.NewLine}<image_attachment path=\"C:\\Temp\\one.png\">@C:\\Temp\\one.png");
        StringAssert.Contains(prompt, $"assistant: Andre{Environment.NewLine}<image_attachment path=\"C:\\Temp\\two.jpg\">@C:\\Temp\\two.jpg");
    }

    [TestMethod]
    public void Build_rejects_unmatched_image_paths()
    {
        var messages = new List<OpenAiMessage>
        {
            new("user", "Bilde") { Images = [new OpenAiImageInput("data:image/png;base64,AA==")] }
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => ImagePromptBuilder.Build(messages, []));
    }
}
