using System.Text;
using System.Text.RegularExpressions;
using AiProxy.Configuration;

namespace AiProxy.Services;

/// <summary>
/// Supplies M365 Copilot with a bounded snapshot of the local workspace. The
/// M365 chat service cannot read local files itself. Every M365 request therefore
/// receives a bounded, secret-filtered snapshot and can write back only the files
/// in that snapshot. This is automatic; callers do not need CLI-style flags.
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
    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12"
    };
    private const int MaxOmittedFileNames = 200;
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

    public async Task<M365WorkspacePrompt> BuildPromptAsync(
        string prompt,
        M365CopilotOptions options,
        CancellationToken cancellationToken,
        string? activeWorkspaceDirectory = null,
        bool clientToolsDeclared = false)
    {
        // The active OpenCode workspace is request-scoped and must win over the
        // server-wide fallback configured on the administration page.
        var root = ResolveWorkspaceDirectory(activeWorkspaceDirectory ?? options.WorkspaceDirectory);
        if (!Directory.Exists(root))
        {
            _logger.LogWarning("M365-workspace {Workspace} finnes ikke; forespørselen sendes uten filer.", root);
            return new M365WorkspacePrompt(prompt, new Dictionary<string, string>(), false);
        }

        var (files, omittedFiles) = await CollectFilesAsync(root, cancellationToken);
        var blocks = string.Join("\n\n", files.Select(file => $"--- file: {file.Label} ---\n{file.Content}\n--- end file ---"));
        var augmentedPrompt = new StringBuilder(prompt);

        if (blocks.Length > 0)
        {
            augmentedPrompt.Append("\n\nThe following is a read-only snapshot of the local workspace. ");
            augmentedPrompt.Append("Use it as the source of truth for this request.\n\n");
            augmentedPrompt.Append(blocks);
        }

        // Without this list the model concludes that a binary document such as a .pptx
        // does not exist, because the snapshot is presented as the source of truth.
        if (omittedFiles.Count > 0)
        {
            augmentedPrompt.Append("\n\nThese workspace files also exist, but their content is not shown (binary, Office document, or too large):\n");
            augmentedPrompt.Append(string.Join("\n", omittedFiles.Take(MaxOmittedFileNames).Select(label => "- " + label)));
            if (omittedFiles.Count > MaxOmittedFileNames)
                augmentedPrompt.Append($"\n- ... and {omittedFiles.Count - MaxOmittedFileNames} more");
        }

        // The m365-copilot-cli `ask --dir --write` flow: complete-file blocks for snapshot files
        // are written back locally. M365 follows this far more reliably than a tool protocol, so
        // it stays available when the client also declares tools.
        if (files.Count > 0)
        {
            augmentedPrompt.Append("\n\nWhen you make a change to a file in the snapshot, output the COMPLETE new content of that file exactly in this format, without a Markdown fence:\n");
            augmentedPrompt.Append("===FILE: <path exactly as shown above>===\n<complete new file content>\n===END FILE===\n");
            augmentedPrompt.Append(clientToolsDeclared
                ? "Only emit a block for a file you are changing. A block is saved to the local file automatically. Other files, including those whose content is not shown, can only be created or changed through a declared client function."
                : "Only emit a block for a file you are changing. Files outside the snapshot cannot be created or changed.");
        }

        _logger.LogInformation("La ved {FileCount} workspace-filer ({ByteCount} byte) for M365 Copilot fra {Workspace}.", files.Count, files.Sum(file => file.ByteCount), root);
        return new M365WorkspacePrompt(
            augmentedPrompt.ToString(),
            files.ToDictionary(file => NormalizeLabel(file.Label), file => file.FullPath, StringComparer.OrdinalIgnoreCase),
            files.Count > 0);
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

                content = await PreserveFinalNewlineAsync(path, content, cancellationToken);
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

    /// <summary>
    /// The block format consumes the line break before ===END FILE===, so a file that ended
    /// with a newline would otherwise lose it on every write.
    /// </summary>
    private static async Task<string> PreserveFinalNewlineAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (content.EndsWith('\n') || !File.Exists(path))
            return content;

        var original = await File.ReadAllTextAsync(path, cancellationToken);
        return original.EndsWith("\r\n", StringComparison.Ordinal) ? content + "\r\n"
            : original.EndsWith('\n') ? content + "\n"
            : content;
    }

    private async Task<(List<WorkspaceFile> Included, List<string> Omitted)> CollectFilesAsync(string root, CancellationToken cancellationToken)
    {
        var candidates = new List<FileInfo>();
        var omitted = new List<string>();
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
                    else if (child is FileInfo binaryFile && IsListableBinaryFile(binaryFile.Name))
                    {
                        omitted.Add(Path.GetRelativePath(root, binaryFile.FullName));
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
                {
                    omitted.Add(Path.GetRelativePath(root, file.FullName));
                    continue;
                }

                var content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                if (content.Contains('\0'))
                {
                    omitted.Add(Path.GetRelativePath(root, file.FullName));
                    continue;
                }

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

        omitted.Sort(StringComparer.OrdinalIgnoreCase);
        return (included, omitted);
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

    /// <summary>Secret, lock and minified files stay unlisted; only their content type excluded them.</summary>
    private static bool IsListableBinaryFile(string name) =>
        SkippedExtensions.Contains(Path.GetExtension(name)) &&
        !LooksLikeSecret(name) &&
        !SecretExtensions.Contains(Path.GetExtension(name));

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
