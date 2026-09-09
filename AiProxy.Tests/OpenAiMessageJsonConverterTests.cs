using System.Text.Json;
using AiProxy.Contracts;

namespace AiProxy.Tests;

[TestClass]
public class OpenAiMessageJsonConverterTests
{
    [TestMethod]
    public void Deserialize_content_array_preserves_text_and_images()
    {
        const string json = """
            {"role":"user","content":[
              {"type":"text","text":"Hva ser du?"},
              {"type":"image_url","image_url":{"url":"data:image/png;base64,iVBORw0KGgo="}},
              {"type":"input_image","image_url":"https://example.test/image.webp"}
            ]}
            """;

        var message = JsonSerializer.Deserialize<OpenAiMessage>(json);

        Assert.IsNotNull(message);
        Assert.AreEqual("user", message.Role);
        Assert.AreEqual("Hva ser du?", message.Content);
        Assert.AreEqual(2, message.Images.Count);
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgo=", message.Images[0].Url);
        Assert.AreEqual("https://example.test/image.webp", message.Images[1].Url);
    }

    [TestMethod]
    public void Deserialize_string_content_remains_backward_compatible()
    {
        var message = JsonSerializer.Deserialize<OpenAiMessage>("""{"role":"user","content":"Hei"}""");

        Assert.IsNotNull(message);
        Assert.AreEqual("Hei", message.Content);
        Assert.AreEqual(0, message.Images.Count);
    }
}
