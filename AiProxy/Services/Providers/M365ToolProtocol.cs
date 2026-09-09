using System.Text;
using System.Text.Json;
using AiProxy.Contracts;

namespace AiProxy.Services.Providers;

/// <summary>
/// The M365 hub has no function-call transport.  This deliberately narrow textual protocol
/// is only a translation layer: the returned call is still executed by the OpenAI client.
/// </summary>
public static class M365ToolProtocol
{
    private const string Fence = "tool_call";
    private static readonly string[] FileChangeTerms = ["write", "update", "create", "modify", "edit", "patch", "skriv", "oppdater", "opprett", "endre"];
    private static readonly string[] FileChangeSubjects = ["file", "fil", "readme", ".md", ".cs", ".json", "document", "dokument"];
    private static readonly string[] WriteToolTerms = ["write", "update", "create", "modify", "edit", "patch", "apply", "skriv", "oppdater", "opprett", "endre"];

    public static bool RequiresDeclaredWriteCall(IReadOnlyList<OpenAiMessage> messages, IReadOnlyList<OpenAiFunctionTool> tools)
    {
        if (!tools.Any(IsLikelyWriteTool))
            return false;

        return messages.Any(message => string.Equals(message.Role, OpenAiConstants.Roles.User, StringComparison.OrdinalIgnoreCase) &&
            ContainsAny(message.Content, FileChangeTerms) && ContainsAny(message.Content, FileChangeSubjects));
    }

    public static string AppendInstruction(string prompt, IReadOnlyList<OpenAiFunctionTool> tools, JsonElement? toolChoice,
        IReadOnlyList<OpenAiToolCall> previousCalls, IReadOnlyList<OpenAiToolResult> results)
    {
        var toolsJson = tools.Select(tool => new
        {
            type = tool.Type,
            name = tool.Name,
            description = tool.Description,
            parameters = tool.Parameters,
            strict = tool.Strict
        });

        var instruction = new StringBuilder();
        instruction.AppendLine("\n\n<client-tools>");
        instruction.AppendLine("The client, not you and not this proxy, executes the following declared functions.");
        instruction.AppendLine("You do have access to these functions through the client. Do not claim that a command, file, or workspace is unavailable when a declared function can perform the needed action; emit the call instead.");
        instruction.AppendLine("Any attached workspace snapshot is read-only evidence. Use a declared client function for a requested file change.");
        instruction.AppendLine("Treat the existing workspace layout as fixed unless the user explicitly asks for a structural change. Do not create, rename, move, or replace solution files, project files, or directories (including a src directory) for a documentation or inspection task.");
        instruction.AppendLine("When the user asks to create or update a specific file, prioritize completing that file. Use only the few discovery calls needed to establish the facts, then issue its write call before further exploration. Do not exhaust the client's tool-call budget by enumerating every file, and do not say a requested file was changed until its tool result confirms it.");
        instruction.AppendLine("For an explicit request to run a command, build, or test, use a declared command-execution function directly when one is available. Do not replace that request with a speculative file search. A zero-result glob is not proof that a project or tests do not exist; glob syntax is tool-specific, so avoid shell-only brace patterns such as `**/*{Test,Tests}.csproj` and prefer simple patterns such as `**/*.csproj` when discovery is actually needed.");
        instruction.AppendLine("If a function is needed, return exactly one call and no surrounding explanation using this exact fenced form:");
        instruction.AppendLine("```tool_call");
        instruction.AppendLine("{\"call_id\":\"call_your_unique_id\",\"name\":\"declared_function_name\",\"arguments\":{}}");
        instruction.AppendLine("```");
        instruction.AppendLine("`name` must exactly match a declared function. `arguments` must be a JSON object for a function tool; a custom tool may instead use a JSON string containing its raw input. Do not call undeclared functions. After a tool result, either issue one next call in the same form or provide the final answer.");
        instruction.AppendLine("Declared functions:");
        instruction.AppendLine(JsonSerializer.Serialize(toolsJson));
        if (toolChoice != null)
            instruction.AppendLine($"Client tool_choice: {toolChoice.Value.GetRawText()}");
        instruction.AppendLine("</client-tools>");

        if (previousCalls.Count > 0)
        {
            instruction.AppendLine("\n<prior-tool-calls>");
            foreach (var call in previousCalls)
                instruction.AppendLine(JsonSerializer.Serialize(new { call_id = call.Id, name = call.Name, arguments = JsonDocument.Parse(call.ArgumentsJson).RootElement }));
            instruction.AppendLine("</prior-tool-calls>");
        }

        if (results.Count > 0)
        {
            instruction.AppendLine("\n<tool-responses>");
            foreach (var result in results)
                instruction.AppendLine(JsonSerializer.Serialize(new { call_id = result.CallId, output = result.Output }));
            instruction.AppendLine("</tool-responses>");
        }

        return prompt + instruction;
    }

    /// <summary>Only a complete, single, declared function call is accepted.</summary>
    public static bool TryParse(string response, IReadOnlyList<OpenAiFunctionTool> declaredTools, out OpenAiProviderToolCall? call)
    {
        call = null;
        if (!TryGetToolPayload(response, out var label, out var json))
            return false;

        if (!string.Equals(label, Fence, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(label, "tool", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(label, "function_call", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(label, "json", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var document = JsonDocument.Parse(NormalizePayload(json));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetName(root, out var name))
                return false;

            var callId = TryString(root, "call_id", out var explicitCallId) || TryString(root, "id", out explicitCallId)
                ? explicitCallId
                : "call_m365_" + Guid.NewGuid().ToString("N");
            var declaredTool = declaredTools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
            if (callId.StartsWith(OpenAiConstants.ToolCalls.ProxyTodoCallIdPrefix, StringComparison.Ordinal) || declaredTool == null)
                return false;

            if (!TryGetArguments(root, declaredTool.Type == OpenAiConstants.ToolCalls.CustomType, out var arguments))
                return false;

            call = new OpenAiProviderToolCall(callId, name, arguments, declaredTool.Type);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsToolFence(string response)
    {
        return response.Contains("```" + Fence, StringComparison.OrdinalIgnoreCase) ||
            response.Contains("```tool\n", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("```tool\r", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("```function_call", StringComparison.OrdinalIgnoreCase) ||
            FindBareToolMarker(response.Trim()) >= 0;
    }

    private static bool TryGetToolPayload(string response, out string label, out string json)
    {
        if (TryGetSingleFence(response, out label, out json))
            return true;

        // Some M365 models use their native-looking marker instead of the requested Markdown
        // fence: "tool_call" followed by one JSON object. They also occasionally precede it
        // with a status sentence. Accept one marker only when its payload consumes the rest of
        // the response, so ordinary prose can never become a tool call.
        label = string.Empty;
        json = string.Empty;
        var trimmed = response.Trim();
        var markerIndex = FindBareToolMarker(trimmed);
        if (markerIndex < 0 || FindBareToolMarker(trimmed, markerIndex + Fence.Length) >= 0)
            return false;

        var remainder = trimmed[(markerIndex + Fence.Length)..].TrimStart();
        if (remainder.Length == 0 || remainder[0] != '{')
            return false;

        label = Fence;
        json = remainder;
        return true;
    }

    private static bool HasBareToolMarker(string response)
    {
        var trimmed = response.TrimStart();
        return trimmed.StartsWith(Fence, StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == Fence.Length || char.IsWhiteSpace(trimmed[Fence.Length]) || trimmed[Fence.Length] == '{');
    }

    private static int FindBareToolMarker(string response, int startIndex = 0)
    {
        for (var index = response.IndexOf(Fence, startIndex, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = response.IndexOf(Fence, index + Fence.Length, StringComparison.OrdinalIgnoreCase))
        {
            var atLineStart = index == 0 || response[index - 1] is '\n' or '\r';
            var marker = response[index..];
            if (atLineStart && HasBareToolMarker(marker))
                return index;
        }

        return -1;
    }

    private static string NormalizePayload(string json)
    {
        var trimmed = json.Trim();

        // GPT-5-based M365 turns occasionally wrap the complete object in one redundant
        // brace pair ({{ ... }}). Peel only that exact outer pair; malformed JSON remains
        // rejected by JsonDocument.Parse below.
        return trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal)
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool TryGetSingleFence(string response, out string label, out string json)
    {
        label = string.Empty;
        json = string.Empty;
        var trimmed = response.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return false;

        var lineEnd = trimmed.IndexOf('\n');
        if (lineEnd < 0)
            return false;

        var close = trimmed.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
        if (close < 0 || !string.IsNullOrWhiteSpace(trimmed[(close + 3)..]))
            return false;

        label = trimmed[3..lineEnd].Trim();
        json = trimmed[(lineEnd + 1)..close].Trim();
        return true;
    }

    private static bool TryGetName(JsonElement root, out string name)
    {
        if (TryString(root, "name", out name) || TryString(root, "tool_name", out name) || TryString(root, "tool", out name))
            return true;

        if (root.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            return TryString(function, "name", out name);

        name = string.Empty;
        return false;
    }

    private static bool TryGetArguments(JsonElement root, bool allowRawString, out string arguments)
    {
        arguments = string.Empty;
        if (!root.TryGetProperty("arguments", out var value) &&
            !root.TryGetProperty("args", out value) &&
            !root.TryGetProperty("input", out value) &&
            !(root.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out value)))
            return false;

        if (value.ValueKind == JsonValueKind.Object)
        {
            arguments = value.GetRawText();
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
            return false;

        var rawInput = value.GetString() ?? string.Empty;
        if (allowRawString)
        {
            arguments = rawInput;
            return true;
        }

        try
        {
            using var argumentsDocument = JsonDocument.Parse(rawInput);
            if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            arguments = argumentsDocument.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsAllowedByChoice(JsonElement? choice, string name)
    {
        if (choice == null)
            return true;

        var value = choice.Value;
        if (value.ValueKind == JsonValueKind.String)
            return !string.Equals(value.GetString(), OpenAiConstants.ToolCalls.ToolChoiceNone, StringComparison.OrdinalIgnoreCase);

        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.GetString() != OpenAiConstants.ToolCalls.FunctionType)
            return true;

        if (TryString(value, "name", out var flatName))
            return string.Equals(flatName, name, StringComparison.Ordinal);

        if (value.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object && TryString(function, "name", out var nestedName))
            return string.Equals(nestedName, name, StringComparison.Ordinal);

        return true;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static bool IsLikelyWriteTool(OpenAiFunctionTool tool) =>
        ContainsAny(tool.Name, WriteToolTerms) || ContainsAny(tool.Description, WriteToolTerms);

    private static bool ContainsAny(string? value, IEnumerable<string> terms) =>
        !string.IsNullOrWhiteSpace(value) && terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
