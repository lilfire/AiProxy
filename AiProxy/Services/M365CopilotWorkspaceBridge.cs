using System.Text;
using System.Text.RegularExpressions;
using AiProxy.Configuration;

namespace AiProxy.Services;

/// <summary>
/// Supplies M365 Copilot with a bounded snapshot of the local workspace. The
/// M365 chat service cannot read local files itself, so this is the equivalent
/// of m365-copilot-cli's <c>--dir</c> and <c>--write</c> options.
/// </summary>
public sealed class M365CopilotWorkspaceBridge
{
    private const int MaxFileBytes = 300_000;
    private const int MaxWorkspaceBytes = 1_500_000;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex FileBlockPattern = new(
        @"===FILE:\s*(.+?)===\r?\n([\s\S]*?)\r?\n===END FILE===",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "dist", "build", "out", "target", "bin", "obj", "coverage", "venv", "__pycache__"
    };
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".flac", ".ogg",
        ".zip", ".tar", ".gz", ".tgz", ".rar", ".7z",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".o", ".obj", ".class", ".pyc",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".db", ".sqlite", ".sqlite3", ".map", ".pem", ".key", ".pfx", ".p12"
    };
    private static readonly HashSet<string> SkippedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "Cargo.lock"
    };

    private readonly IHostEnvironment _environment;
    private readonly ILogger<M365CopilotWorkspaceBridge> _logger;

    public M365CopilotWorkspaceBridge(IHostEnvironment environment, ILogger<M365CopilotWorkspaceBridge> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task<M365WorkspacePrompt> BuildPromptAsync(string prompt, M365CopilotOptions options, CancellationToken cancellationToken)
    {
        if (!options.IncludeWorkspaceFiles)
            return new M365WorkspacePrompt(prompt, new Dictionary<string, string>(), false);

        var root = ResolveWorkspaceDirectory(options.WorkspaceDirectory);
        if (!Directory.Exists(root))
        {
            _logger.LogWarning("M365-workspace {Workspace} finnes ikke; forespørselen sendes uten filer.", root);
            return new M365WorkspacePrompt(prompt, new Dictionary<string, string>(), false);
        }

        var files = await CollectFilesAsync(root, cancellationToken);
        var blocks = string.Join("\n\n", files.Select(file => $"--- file: {file.Label} ---\n{file.Content}\n--- end file ---"));
        var augmentedPrompt = new StringBuilder(prompt);

        if (blocks.Length > 0)
        {
            augmentedPrompt.Append("\n\nThe following is a read-only snapshot of the local workspace. ");
            augmentedPrompt.Append("Use it as the source of truth for this request.\n\n");
            augmentedPrompt.Append(blocks);
        }

        if (options.EnableWorkspaceWrites && files.Count > 0)
        {
            augmentedPrompt.Append("\n\nWhen you make a change to a file in the snapshot, output the COMPLETE new content of that file exactly in this format, without a Markdown fence:\n");
            augmentedPrompt.Append("===FILE: <path exactly as shown above>===\n<complete new file content>\n===END FILE===\n");
            augmentedPrompt.Append("Only emit a block for a file you are changing. Files outside the snapshot cannot be created or changed.");
        }

        _logger.LogInformation("La ved {FileCount} workspace-filer ({ByteCount} byte) for M365 Copilot fra {Workspace}.", files.Count, files.Sum(file => file.ByteCount), root);
        return new M365WorkspacePrompt(
            augmentedPrompt.ToString(),
            files.ToDictionary(file => NormalizeLabel(file.Label), file => file.FullPath, StringComparer.OrdinalIgnoreCase),
            options.EnableWorkspaceWrites && files.Count > 0);
    }

    public async Task<M365WorkspaceWriteResult> ApplyWritesAsync(string answer, M365WorkspacePrompt workspace, bool createBackups, CancellationToken cancellationToken)
    {
        if (!workspace.WritesEnabled)
            return new M365WorkspaceWriteResult(answer, []);

        var written = new List<string>();
        var skipped = new List<string>();
        foreach (Match match in FileBlockPattern.Matches(answer))
        {
            var label = match.Groups[1].Value;
            var content = match.Groups[2].Value;
            if (!workspace.Files.TryGetValue(NormalizeLabel(label), out var path))
            {
                skipped.Add($"{label.Trim()} (ikke i workspace-snapshotet)");
                continue;
            }

            if (Utf8WithoutBom.GetByteCount(content) > MaxFileBytes)
            {
                skipped.Add($"{label.Trim()} (foreslått innhold er for stort)");
                continue;
            }

            try
            {
                if (createBackups && File.Exists(path))
                    File.Copy(path, path + ".bak", overwrite: true);

                await File.WriteAllTextAsync(path, content, Utf8WithoutBom, cancellationToken);
                written.Add(label.Trim());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Kunne ikke skrive M365-endring til {File}.", path);
                skipped.Add($"{label.Trim()} (skriving feilet)");
            }
        }

        var cleaned = FileBlockPattern.Replace(answer, string.Empty).Trim();
        if (written.Count > 0)
        {
            var backupNote = createBackups ? " .bak-sikkerhetskopi er lagret." : string.Empty;
            cleaned = $"{cleaned}\n\nAiProxy oppdaterte {written.Count} workspace-fil(er): {string.Join(", ", written)}.{backupNote}".Trim();
            _logger.LogInformation("M365 Copilot oppdaterte {FileCount} workspace-filer: {Files}", written.Count, string.Join(", ", written));
        }

        if (skipped.Count > 0)
            _logger.LogWarning("M365 Copilot foreslo workspace-endringer som ble ignorert: {Skipped}", string.Join("; ", skipped));

        return new M365WorkspaceWriteResult(cleaned, written);
    }

    private async Task<List<WorkspaceFile>> CollectFilesAsync(string root, CancellationToken cancellationToken)
    {
        var candidates = new List<FileInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            try
            {
                foreach (var child in directory.EnumerateFileSystemInfos())
                {
                    if (child is DirectoryInfo childDirectory)
                    {
                        if (!ShouldSkipDirectory(childDirectory.Name) && !childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            stack.Push(childDirectory);
                    }
                    else if (child is FileInfo file && !ShouldSkipFile(file.Name))
                    {
                        candidates.Add(file);
                    }
                }
            }
            catch (IOException)
            {
                // An unreadable directory is not a reason to abandon an otherwise usable workspace.
            }
            catch (UnauthorizedAccessException)
            {
                // See above.
            }
        }

        var included = new List<WorkspaceFile>();
        var usedBytes = 0L;
        foreach (var file in candidates.OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (file.Length > MaxFileBytes || usedBytes + file.Length > MaxWorkspaceBytes)
                    continue;

                var content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                if (content.Contains('\0'))
                    continue;

                usedBytes += file.Length;
                included.Add(new WorkspaceFile(Path.GetRelativePath(root, file.FullName), file.FullName, content, file.Length));
            }
            catch (IOException)
            {
                // The file may have changed or disappeared while the directory was scanned.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat an unreadable file as excluded from the snapshot.
            }
        }

        return included;
    }

    private string ResolveWorkspaceDirectory(string? configuredDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory) ? _environment.ContentRootPath : Path.GetFullPath(configuredDirectory);

    private static bool ShouldSkipDirectory(string name) => name.StartsWith('.') || SkippedDirectoryNames.Contains(name);

    private static bool ShouldSkipFile(string name) =>
        SkippedFileNames.Contains(name) ||
        name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase) ||
        LooksLikeSecret(name) ||
        SkippedExtensions.Contains(Path.GetExtension(name));

    private static bool LooksLikeSecret(string name) =>
        name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLabel(string label) => label.Trim().Trim('`', '\'', '"').Trim().Replace('\\', '/');

    private sealed record WorkspaceFile(string Label, string FullPath, string Content, long ByteCount);
}

public sealed record M365WorkspacePrompt(string Prompt, IReadOnlyDictionary<string, string> Files, bool WritesEnabled);

public sealed record M365WorkspaceWriteResult(string Answer, IReadOnlyList<string> WrittenFiles);
