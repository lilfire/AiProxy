using System.Text;
using System.Text.Json;
using AiProxy.Contracts;

namespace AiProxy.Services.Tools;

/// <summary>
/// Felles tekstprotokoll for providere uten en egen transport for funksjonskall. Protokollen er
/// bare et oversettelseslag: kallet den returnerer utføres av klienten, aldri av AiProxy.
/// </summary>
public static class ClientToolProtocol
{
    private const string FENCE = "tool_call";

    public static string AppendInstruction(
        string prompt,
        IReadOnlyList<OpenAiFunctionTool> tools,
        JsonElement? toolChoice,
        IReadOnlyList<OpenAiToolCall> previousCalls,
        IReadOnlyList<OpenAiToolResult> results,
        ClientToolInstructionOptions options)
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
        foreach (var paragraph in options.ExtraParagraphs)
            instruction.AppendLine(paragraph);
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

    /// <summary>
    /// Returnerer null når svaret ikke inneholder nøyaktig ett komplett, deklarert kall.
    /// <paramref name="callIdPrefix"/> brukes bare når modellen ikke oppgav en egen id.
    /// </summary>
    public static OpenAiProviderToolCall? Parse(string response, IReadOnlyList<OpenAiFunctionTool> declaredTools, string callIdPrefix)
    {
        if (!TryGetToolPayload(response, out var label, out var json))
            return null;

        if (!IsToolFenceLabel(label) && !string.Equals(label, "json", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var document = JsonDocument.Parse(NormalizePayload(json));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetName(root, out var name))
                return null;

            var callId = TryString(root, "call_id", out var explicitCallId) || TryString(root, "id", out explicitCallId)
                ? explicitCallId
                : callIdPrefix + Guid.NewGuid().ToString("N");
            var declaredTool = declaredTools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
            if (callId.StartsWith(OpenAiConstants.ToolCalls.ProxyTodoCallIdPrefix, StringComparison.Ordinal) || declaredTool == null)
                return null;

            if (!TryGetArguments(root, declaredTool.Type == OpenAiConstants.ToolCalls.CustomType, out var arguments))
                return null;

            return new OpenAiProviderToolCall(callId, name, arguments, declaredTool.Type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsToolFence(string response)
    {
        return response.Contains("```" + FENCE, StringComparison.OrdinalIgnoreCase) ||
            response.Contains("```tool\n", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("```tool\r", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("```function_call", StringComparison.OrdinalIgnoreCase) ||
            FindBareToolMarker(response.Trim()) >= 0;
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

    /// <summary>
    /// Fanger den ufullstendige PR-gjennomgangsturen der modellen sier at den trenger filer,
    /// selv om klienten allerede har deklarert repository-filfunksjonen.
    /// </summary>
    public static bool RequiresAzureDevOpsFileCall(string response, IReadOnlyList<OpenAiFunctionTool> declaredTools)
    {
        if (!declaredTools.Any(tool => string.Equals(tool.Name, "metamcp_azure-devops__repo_file", StringComparison.Ordinal)))
            return false;

        var normalized = response.ToLowerInvariant();
        var needsFileData = normalized.Contains("need") &&
            (normalized.Contains("file content") || normalized.Contains("changed file") ||
                normalized.Contains("file response") || normalized.Contains("remaining file") ||
                normalized.Contains("both revision") || normalized.Contains("source and target"));
        var reportsUnavailable = (normalized.Contains("cannot") || normalized.Contains("unavailable") ||
            normalized.Contains("could not") || normalized.Contains("couldn't")) &&
            (normalized.Contains("file") || normalized.Contains("diff") || normalized.Contains("repository"));

        return needsFileData || reportsUnavailable;
    }

    private static bool TryGetToolPayload(string response, out string label, out string json)
    {
        if (TryGetSingleFence(response, out label, out json))
            return true;

        if (TryGetTrailingToolFence(response, out label, out json))
            return true;

        // Noen modeller bruker sin egen markør i stedet for det etterspurte Markdown-gjerdet:
        // "tool_call" etterfulgt av ett JSON-objekt, av og til med en statussetning foran.
        // Godta bare én markør, og bare når nyttelasten fyller resten av svaret, slik at vanlig
        // prosa aldri kan bli et verktøykall.
        label = string.Empty;
        json = string.Empty;
        var trimmed = response.Trim();
        var markerIndex = FindBareToolMarker(trimmed);
        if (markerIndex < 0 || FindBareToolMarker(trimmed, markerIndex + FENCE.Length) >= 0)
            return false;

        var remainder = trimmed[(markerIndex + FENCE.Length)..].TrimStart();
        if (remainder.Length == 0 || remainder[0] != '{')
            return false;

        label = FENCE;
        json = remainder;
        return true;
    }

    private static bool HasBareToolMarker(string response)
    {
        var trimmed = response.TrimStart();
        return trimmed.StartsWith(FENCE, StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == FENCE.Length || char.IsWhiteSpace(trimmed[FENCE.Length]) || trimmed[FENCE.Length] == '{');
    }

    private static int FindBareToolMarker(string response, int startIndex = 0)
    {
        for (var index = response.IndexOf(FENCE, startIndex, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = response.IndexOf(FENCE, index + FENCE.Length, StringComparison.OrdinalIgnoreCase))
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

        // Enkelte modeller pakker hele objektet i ett overflødig klammepar ({{ ... }}). Skrell
        // bare det ene ytre paret; ugyldig JSON avvises fortsatt av JsonDocument.Parse.
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

    /// <summary>
    /// Resonnerende modeller skriver ofte en statussetning før gjerdet, og strømmen kan slå de to
    /// sammen uten linjeskift ("...presentasjonen.```tool_call"). Godta det bare for en eksplisitt
    /// verktøyetikett, og bare når det ene verktøygjerdet avslutter svaret, slik at en vanlig
    /// Markdown-kodeblokk i prosa aldri blir et kall.
    /// </summary>
    private static bool TryGetTrailingToolFence(string response, out string label, out string json)
    {
        label = string.Empty;
        json = string.Empty;
        var trimmed = response.Trim();
        if (!trimmed.EndsWith("```", StringComparison.Ordinal))
            return false;

        var open = FindToolFenceOpening(trimmed);
        if (open < 0 || FindToolFenceOpening(trimmed, open + 3) >= 0)
            return false;

        var lineEnd = trimmed.IndexOf('\n', open);
        if (lineEnd < 0 || lineEnd + 1 > trimmed.Length - 3)
            return false;

        label = trimmed[(open + 3)..lineEnd].Trim();
        json = trimmed[(lineEnd + 1)..^3].Trim();
        return true;
    }

    private static int FindToolFenceOpening(string response, int startIndex = 0)
    {
        for (var index = response.IndexOf("```", startIndex, StringComparison.Ordinal);
             index >= 0;
             index = response.IndexOf("```", index + 3, StringComparison.Ordinal))
        {
            var lineEnd = response.IndexOf('\n', index);
            var label = (lineEnd < 0 ? response[(index + 3)..] : response[(index + 3)..lineEnd]).Trim();
            if (IsToolFenceLabel(label))
                return index;
        }

        return -1;
    }

    private static bool IsToolFenceLabel(string label) =>
        string.Equals(label, FENCE, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(label, "tool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(label, "function_call", StringComparison.OrdinalIgnoreCase);

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

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }
}
