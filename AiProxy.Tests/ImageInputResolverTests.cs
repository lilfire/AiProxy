using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class ImageInputResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_data_png_writes_a_temporary_png_and_deletes_it()
    {
        // PNG-signaturen er tilstrekkelig for inngangsvalideringen; testen gjelder transport og opprydding.
        var data = Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var resolver = new ImageInputResolver();

        var images = await resolver.ResolveAsync([new OpenAiImageInput($"data:image/png;base64,{data}")]);
        var path = images.Paths.Single();

        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(".png", Path.GetExtension(path));
        await images.DisposeAsync();
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task ResolveAsync_rejects_unsupported_image_format()
    {
        var data = Convert.ToBase64String("not-an-image"u8.ToArray());
        var resolver = new ImageInputResolver();

        await Assert.ThrowsExactlyAsync<ImageInputException>(() =>
            resolver.ResolveAsync([new OpenAiImageInput($"data:image/gif;base64,{data}")]));
    }
}
