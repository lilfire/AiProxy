using System.Text.Json;
using AiProxy.Services;
using AiProxy.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public sealed class CodexModelCatalogTests
{
    [TestMethod]
    public async Task Discovers_all_visible_pages_and_uses_saved_models_after_restart_and_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), "aiproxy-codex-models-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var runner = new CatalogRunner();
            runner.Responses.Enqueue("""
                {"data":[{"model":"gpt-a"},{"model":"gpt-hidden","hidden":true}],"nextCursor":"page-2"}
                """);
            runner.Responses.Enqueue("""
                {"data":[{"model":"gpt-b"},{"model":"gpt-a"}],"nextCursor":null}
                """);
            var catalog = new CodexModelCatalog(runner, NullLogger<CodexModelCatalog>.Instance, path);

            var models = await catalog.GetModelIdsAsync();

            CollectionAssert.AreEqual(new[] { "gpt-a", "gpt-b" }, models.ToArray());
            Assert.AreEqual(2, runner.Cursors.Count);
            Assert.IsNull(runner.Cursors[0]);
            Assert.AreEqual("page-2", runner.Cursors[1]);
            Assert.IsTrue(File.Exists(path));

            var failedRunner = new CatalogRunner { Failure = new InvalidOperationException("offline") };
            var restartedCatalog = new CodexModelCatalog(failedRunner, NullLogger<CodexModelCatalog>.Instance, path);
            CollectionAssert.AreEqual(new[] { "gpt-a", "gpt-b" }, (await restartedCatalog.GetModelIdsAsync()).ToArray());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Rejects_invalid_catalog_without_replacing_saved_list()
    {
        var path = Path.Combine(Path.GetTempPath(), "aiproxy-codex-models-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var runner = new CatalogRunner();
            runner.Responses.Enqueue("""{"data":[{"model":"gpt-good"}],"nextCursor":null}""");
            var catalog = new CodexModelCatalog(runner, NullLogger<CodexModelCatalog>.Instance, path);
            await catalog.GetModelIdsAsync();

            runner.Responses.Enqueue("""{"unexpected":[]}""");
            CollectionAssert.AreEqual(new[] { "gpt-good" }, (await catalog.GetModelIdsAsync()).ToArray());
            Assert.IsTrue(File.ReadAllText(path).Contains("gpt-good"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class CatalogRunner() : ShellCommandRunner(
        new ExecutablePathResolver(), NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings())
    {
        public Queue<string> Responses { get; } = new();
        public List<string?> Cursors { get; } = new();
        public Exception? Failure { get; set; }

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
            Assert.AreEqual(1L, initializeResponseId);
            Assert.AreEqual(2L, responseId);
            Assert.AreEqual(30, timeoutSeconds);
            var requests = requestLines.ToArray();
            using var listRequest = JsonDocument.Parse(requests[2]);
            Assert.AreEqual("model/list", listRequest.RootElement.GetProperty("method").GetString());
            var parameters = listRequest.RootElement.GetProperty("params");
            Assert.IsFalse(parameters.GetProperty("includeHidden").GetBoolean());
            Cursors.Add(parameters.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null);

            if (Failure != null)
                throw Failure;
            return Task.FromResult(Responses.Dequeue());
        }
    }
}
