using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Contracts;

/// <summary>Verktøydeklarasjon slik Responses-API-et sender den: flat, ikke nøstet under "function".</summary>
public class OpenAiResponsesTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }

    /// <summary>
    /// Enkelte OpenAI-kompatible klienter gjenbruker Chat Completions-formen i en Responses-forespørsel.
    /// Støtt den nestede deklarasjonen også, men behold Responses sin flate form som førstevalg.
    /// </summary>
    [JsonPropertyName("function")]
    public OpenAiChatFunction? Function { get; set; }

    [JsonPropertyName("custom")]
    public OpenAiChatFunction? Custom { get; set; }

    [JsonIgnore]
    public string? NameOrNestedName =>
        !string.IsNullOrWhiteSpace(Name) ? Name : Function?.Name ?? Custom?.Name;

    public OpenAiFunctionTool? ToCallableTool()
    {
        if (Type != OpenAiConstants.ToolCalls.FunctionType && Type != OpenAiConstants.ToolCalls.CustomType)
            return null;

        var nested = Type == OpenAiConstants.ToolCalls.CustomType ? Custom ?? Function : Function ?? Custom;
        var name = !string.IsNullOrWhiteSpace(Name) ? Name : nested?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new OpenAiFunctionTool(
            name,
            Description ?? nested?.Description,
            Parameters ?? nested?.Parameters,
            Strict ?? nested?.Strict,
            Type);
    }
}
