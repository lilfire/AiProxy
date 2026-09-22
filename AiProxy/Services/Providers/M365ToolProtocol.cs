using System.Text.Json;
using AiProxy.Contracts;
using AiProxy.Services.Tools;

namespace AiProxy.Services.Providers;

/// <summary>
/// The M365 hub has no function-call transport.  This deliberately narrow textual protocol
/// is only a translation layer: the returned call is still executed by the OpenAI client.
/// Den generiske delen ligger i <see cref="ClientToolProtocol"/>; her blir bare det som
/// gjelder M365 spesifikt.
/// </summary>
public static class M365ToolProtocol
{
    private const string CALLIDPREFIX = "call_m365_";
    private static readonly string[] FileChangeTerms = ["write", "update", "create", "modify", "edit", "patch", "apply", "skriv", "oppdater", "opprett", "endre", "rette", "rett", "fikse"];
    private static readonly string[] FileChangeSubjects = ["file", "fil", "filen", "readme", ".md", ".cs", ".json", "document", "dokument", "koden"];
    private static readonly string[] WriteToolTerms = ["write", "update", "create", "modify", "edit", "patch", "apply", "skriv", "oppdater", "opprett", "endre", "rette", "rett", "fikse"];

    /// <summary>Avsnittene som bare gjelder M365: workspace-snapshot og Copilots egen sandkasse.</summary>
    private static readonly string[] InstructionParagraphs =
    [
        "To change a text file shown in the workspace snapshot, return its complete new content as a ===FILE block; it is saved locally. Use a declared client function for everything else, such as files not in the snapshot, binary or Office files, new files, and commands.",
        "Do not use built-in tools such as container.exec, python, or a code interpreter: they run in a remote sandbox (/mnt/data) that does not contain the user's files. The user's local workspace, including files whose content is not in the snapshot, is reachable only through the declared client functions.",
        "Treat the existing workspace layout as fixed unless the user explicitly asks for a structural change. Do not create, rename, move, or replace solution files, project files, or directories (including a src directory) for a documentation or inspection task.",
        "When the user asks to create or update a specific file, prioritize completing that file. Use only the few discovery calls needed to establish the facts, then issue its write call before further exploration. Do not exhaust the client's tool-call budget by enumerating every file, and do not say a requested file was changed until its tool result confirms it.",
        "For an explicit request to run a command, build, or test, use a declared command-execution function directly when one is available. Do not replace that request with a speculative file search. A zero-result glob is not proof that a project or tests do not exist; glob syntax is tool-specific, so avoid shell-only brace patterns such as `**/*{Test,Tests}.csproj` and prefer simple patterns such as `**/*.csproj` when discovery is actually needed."
    ];

    public static bool RequiresDeclaredWriteCall(IReadOnlyList<OpenAiMessage> messages, IReadOnlyList<OpenAiFunctionTool> tools)
    {
        if (!tools.Any(IsLikelyWriteTool))
            return false;

        var userMessages = messages
            .Where(m => string.Equals(m.Role, OpenAiConstants.Roles.User, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (userMessages.Count == 0)
            return false;

        // Sjekk den siste brukermeldingen. Hvis den er kort (< 200 tegn) og ikke
        // matcher, sjekk også den forrige — men bare hvis den også er kort. Dette
        // håndterer "lag plan for å rette filen" → "utfør" uten å treffe på
        // systemprompter som OpenCode sender som user-rollen (> 500 tegn).
        var last = userMessages[^1].Content;
        if (!string.IsNullOrWhiteSpace(last) && MatchFileChange(last))
            return true;

        if (last != null && last.Length < 200 && userMessages.Count >= 2)
        {
            var previous = userMessages[^2].Content;
            if (!string.IsNullOrWhiteSpace(previous) && previous.Length < 200 && MatchFileChange(previous))
                return true;
        }

        return false;
    }

    public static string AppendInstruction(string prompt, IReadOnlyList<OpenAiFunctionTool> tools, JsonElement? toolChoice,
        IReadOnlyList<OpenAiToolCall> previousCalls, IReadOnlyList<OpenAiToolResult> results) =>
        ClientToolProtocol.AppendInstruction(prompt, tools, toolChoice, previousCalls, results,
            new ClientToolInstructionOptions { ExtraParagraphs = InstructionParagraphs });

    /// <summary>Copilot's own code container, which cannot see the client's local files.</summary>
    public static bool IsSandboxInvocation(string name) =>
        name.StartsWith("container.", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("python", StringComparison.OrdinalIgnoreCase);

    public static string BuildSandboxCorrection(string? workingDirectory)
    {
        var workspace = string.IsNullOrWhiteSpace(workingDirectory)
            ? "the user's local machine"
            : $"the local directory {workingDirectory} on the user's machine";
        return "\n\n<sandbox-correction>Your previous attempt used Copilot's built-in code container (container.exec on /mnt/data). " +
            $"That remote sandbox does not contain the user's files; the workspace is {workspace}. " +
            "Inspect or change local files only by returning a declared client function in the ```tool_call form. " +
            "Do not ask the user to upload a file that the client can reach.</sandbox-correction>";
    }

    /// <summary>Only a complete, single, declared function call is accepted.</summary>
    public static bool TryParse(string response, IReadOnlyList<OpenAiFunctionTool> declaredTools, out OpenAiProviderToolCall? call)
    {
        call = ClientToolProtocol.Parse(response, declaredTools, CALLIDPREFIX);
        return call != null;
    }

    public static bool IsToolFence(string response) => ClientToolProtocol.IsToolFence(response);

    public static bool IsAllowedByChoice(JsonElement? choice, string name) => ClientToolProtocol.IsAllowedByChoice(choice, name);

    internal static bool IsLikelyWriteTool(OpenAiFunctionTool tool) =>
        ContainsAny(tool.Name, WriteToolTerms) || ContainsAny(tool.Description, WriteToolTerms);

    private static bool MatchFileChange(string content) =>
        ContainsAny(content, FileChangeTerms) && ContainsAny(content, FileChangeSubjects);

    private static bool ContainsAny(string? value, IEnumerable<string> terms) =>
        !string.IsNullOrWhiteSpace(value) && terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
