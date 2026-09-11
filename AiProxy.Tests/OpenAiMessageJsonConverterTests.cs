using System.Text.Json;
using AiProxy.Contracts;

namespace AiProxy.Tests;

[TestClass]
public class OpenAiMessageJsonConverterTests
{
    [TestMethod]
    public void Serialize_message_with_images_writes_openai_content_array()
    {
        var message = new OpenAiMessage("user", "Hva viser dette?")
        {
            Images = [new OpenAiImageInput("data:image/png;base64,iVBORw0KGgo=")]
        };

        var json = JsonSerializer.Serialize(message);
        using var document = JsonDocument.Parse(json);
        var content = document.RootElement.GetProperty("content");

        Assert.AreEqual(JsonValueKind.Array, content.ValueKind);
        Assert.AreEqual("text", content[0].GetProperty("type").GetString());
        Assert.AreEqual("Hva viser dette?", content[0].GetProperty("text").GetString());
        Assert.AreEqual("image_url", content[1].GetProperty("type").GetString());
        Assert.AreEqual("data:image/png;base64,iVBORw0KGgo=", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

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
