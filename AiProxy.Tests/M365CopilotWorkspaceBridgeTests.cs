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
            var options = new M365CopilotOptions { WorkspaceDirectory = root };

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

    [TestMethod]
    public async Task Lists_omitted_documents_and_keeps_file_block_writes_alongside_declared_client_tools()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aiproxy-m365-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "readme");
            await File.WriteAllBytesAsync(Path.Combine(root, "Uni_Hackathon_2026_Avare 5.pptx"), [0x50, 0x4B, 0x03, 0x04]);
            await File.WriteAllTextAsync(Path.Combine(root, "server.key"), "PRIVATE");
            var bridge = new M365CopilotWorkspaceBridge(new TestHostEnvironment(root), NullLogger<M365CopilotWorkspaceBridge>.Instance);

            var workspace = await bridge.BuildPromptAsync(
                "Update the presentation", new M365CopilotOptions { WorkspaceDirectory = root }, CancellationToken.None,
                clientToolsDeclared: true);

            StringAssert.Contains(workspace.Prompt, "- Uni_Hackathon_2026_Avare 5.pptx");
            Assert.IsFalse(workspace.Prompt.Contains("server.key", StringComparison.Ordinal));
            StringAssert.Contains(workspace.Prompt, "===FILE: <path exactly as shown above>===");
            StringAssert.Contains(workspace.Prompt, "through a declared client function");
            Assert.IsTrue(workspace.WritesEnabled);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task File_block_write_keeps_the_original_final_newline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aiproxy-m365-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var readme = Path.Combine(root, "README.md");
            await File.WriteAllTextAsync(readme, "# Demo\n");
            var bridge = new M365CopilotWorkspaceBridge(new TestHostEnvironment(root), NullLogger<M365CopilotWorkspaceBridge>.Instance);
            var workspace = await bridge.BuildPromptAsync("Update README", new M365CopilotOptions { WorkspaceDirectory = root }, CancellationToken.None);

            await bridge.ApplyWritesAsync("===FILE: README.md===\n# Hackathon\n===END FILE===", workspace, createBackups: false, CancellationToken.None);

            Assert.AreEqual("# Hackathon\n", await File.ReadAllTextAsync(readme));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Active_request_workspace_overrides_the_configured_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aiproxy-m365-workspace-{Guid.NewGuid():N}");
        var activeRoot = Path.Combine(Path.GetTempPath(), $"aiproxy-m365-active-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(activeRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "fallback.txt"), "fallback");
            var activeFile = Path.Combine(activeRoot, "active.txt");
            await File.WriteAllTextAsync(activeFile, "before");
            var bridge = new M365CopilotWorkspaceBridge(new TestHostEnvironment(root), NullLogger<M365CopilotWorkspaceBridge>.Instance);

            var workspace = await bridge.BuildPromptAsync(
                "Update the active file", new M365CopilotOptions { WorkspaceDirectory = root }, CancellationToken.None, activeRoot);

            Assert.IsTrue(workspace.Files.ContainsKey("active.txt"));
            Assert.IsFalse(workspace.Files.ContainsKey("fallback.txt"));
            var result = await bridge.ApplyWritesAsync("""
                ===FILE: active.txt===
                after
                ===END FILE===
                """, workspace, createBackups: false, CancellationToken.None);

            Assert.AreEqual("after", await File.ReadAllTextAsync(activeFile));
            CollectionAssert.AreEqual(new[] { "active.txt" }, result.WrittenFiles.ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(activeRoot))
                Directory.Delete(activeRoot, recursive: true);
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
