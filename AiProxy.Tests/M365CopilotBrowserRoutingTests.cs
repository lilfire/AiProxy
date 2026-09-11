using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Providers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public sealed class M365CopilotBrowserRoutingTests
{
    [TestMethod]
    public async Task Image_turn_and_later_turn_use_the_dedicated_browser_session()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aiproxy-m365-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var settings = new TestRuntimeSettings();
            var browser = new FakeBrowserClient();
            var provider = new M365CopilotProvider(
                new M365CopilotTokenProvider(settings, NullLogger<M365CopilotTokenProvider>.Instance),
                new M365CopilotSessionStore(),
                new M365CopilotWorkspaceBridge(new TestHostEnvironment(root), NullLogger<M365CopilotWorkspaceBridge>.Instance),
                settings,
                NullLogger<M365CopilotProvider>.Instance,
                new FakeImageResolver(),
                browser);
            var imageRequest = new OpenAiChatRequest
            {
                Model = "M365-auto",
                Messages = [new OpenAiMessage("user", "What is shown?") { Images = [new OpenAiImageInput("data:image/png;base64,AA==")] }]
            };

            Assert.AreEqual("browser answer", await provider.ExecuteAsync(imageRequest, "conversation", CancellationToken.None));
            Assert.AreEqual(1, browser.Calls[0].ImageCount);

            var textRequest = new OpenAiChatRequest
            {
                Model = "M365-auto",
                Messages = [new OpenAiMessage("user", "Continue")]
            };
            Assert.AreEqual("browser answer", await provider.ExecuteAsync(textRequest, "conversation", CancellationToken.None));
            Assert.AreEqual(2, browser.Calls.Count);
            Assert.AreEqual(0, browser.Calls[1].ImageCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeBrowserClient : IM365CopilotBrowserClient
    {
        public List<(string SessionId, int ImageCount)> Calls { get; } = [];
        public bool UsesBrowserSession(string clientSessionId) => Calls.Any(call => call.SessionId == clientSessionId);
        public Task<string> ExecuteAsync(string clientSessionId, string prompt, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken = default)
        {
            Calls.Add((clientSessionId, imagePaths.Count));
            return Task.FromResult("browser answer");
        }
        public Task<M365BrowserProfileStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new M365BrowserProfileStatus(false, false, ""));
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeImageResolver : IImageInputResolver
    {
        public Task<ResolvedImages> ResolveAsync(IEnumerable<OpenAiImageInput> inputs, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResolvedImages(inputs.Any() ? ["image.png"] : []));
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AiProxy.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
