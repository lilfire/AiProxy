using AiProxy.Configuration;
using AiProxy.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public sealed class M365CopilotWorkspaceBridgeTests
{
    [TestMethod]
    public async Task Includes_safe_workspace_files_and_writes_only_included_file_blocks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aiproxy-m365-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        try
        {
            var sourceFile = Path.Combine(root, "Program.cs");
            await File.WriteAllTextAsync(sourceFile, "before");
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "TOP_SECRET=yes");
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "generated.cs"), "generated");

            var bridge = new M365CopilotWorkspaceBridge(new TestHostEnvironment(root), NullLogger<M365CopilotWorkspaceBridge>.Instance);
            var options = new M365CopilotOptions { WorkspaceDirectory = root, IncludeWorkspaceFiles = true, EnableWorkspaceWrites = true };

            var workspace = await bridge.BuildPromptAsync("Fix the project", options, CancellationToken.None);

            StringAssert.Contains(workspace.Prompt, "--- file: Program.cs ---");
            Assert.IsFalse(workspace.Prompt.Contains("TOP_SECRET", StringComparison.Ordinal));
            Assert.IsFalse(workspace.Prompt.Contains("generated", StringComparison.Ordinal));
            Assert.IsTrue(workspace.Files.ContainsKey("Program.cs"));

            var result = await bridge.ApplyWritesAsync("""
                Updated it.
                ===FILE: Program.cs===
                after
                ===END FILE===
                ===FILE: outside.cs===
                should not be written
                ===END FILE===
                """, workspace, createBackups: false, CancellationToken.None);

            Assert.AreEqual("after", await File.ReadAllTextAsync(sourceFile));
            Assert.IsFalse(File.Exists(sourceFile + ".bak"));
            CollectionAssert.AreEqual(new[] { "Program.cs" }, result.WrittenFiles.ToArray());
            Assert.IsFalse(result.Answer.Contains("===FILE:", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AiProxy.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
