using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ProviderQuotaServiceTests
{
    [TestMethod]
    public async Task Codex_quota_allows_cold_start_and_parses_unix_reset_times()
    {
        var runner = new CodexQuotaShellRunner("""
            {"rateLimits":{"primary":{"usedPercent":52,"windowDurationMins":300,"resetsAt":1788950319},"secondary":{"usedPercent":18,"windowDurationMins":10080,"resetsAt":1789467622}}}
            """);
        var service = new ProviderQuotaService(new UnusedHttpClientFactory(), runner, NullLogger<ProviderQuotaService>.Instance);

        var snapshot = await service.GetSnapshotAsync(OpenAiConstants.Providers.Codex);

        Assert.IsNotNull(snapshot);
        Assert.IsNull(snapshot.Error);
        Assert.AreEqual(30, runner.TimeoutSeconds);
        Assert.AreEqual(2, snapshot.Windows.Count);
        Assert.AreEqual("5-timersøkt", snapshot.Windows[0].Name);
        Assert.AreEqual(52, snapshot.Windows[0].UsedPercent);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1788950319), snapshot.Windows[0].ResetsAt);
        Assert.AreEqual("7-dagersgrense", snapshot.Windows[1].Name);
    }

    private sealed class CodexQuotaShellRunner(string response) : ShellCommandRunner(
        new ExecutablePathResolver(), NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings())
    {
        public int? TimeoutSeconds { get; private set; }

        public override Task<string> RunJsonRpcAsync(
            string command,
            IEnumerable<string> argumentSegments,
            IEnumerable<string> requestLines,
            long responseId,
            int timeoutSeconds,
            long? initializeResponseId = null,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("codex", command);
            CollectionAssert.AreEqual(new[] { "app-server", "--stdio" }, argumentSegments.ToArray());
            Assert.AreEqual(2L, responseId);
            Assert.AreEqual(1L, initializeResponseId);
            TimeoutSeconds = timeoutSeconds;
            return Task.FromResult(response);
        }
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new AssertFailedException("HTTP should not be used for Codex quota reads.");
    }
}
