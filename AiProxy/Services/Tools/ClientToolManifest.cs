using AiProxy.Contracts;
using Microsoft.Extensions.Logging;

namespace AiProxy.Services.Tools;

/// <summary>
/// Normaliserer og logger klientens verktøykatalog uten å logge schemaer eller argumenter.
/// Dette gjør det mulig å se om et MCP-verktøy manglet allerede ved inngangen til proxyen.
/// </summary>
public static class ClientToolManifest
{
    public static List<OpenAiFunctionTool> Normalize(
        IReadOnlyList<OpenAiChatTool>? declarations,
        string endpoint,
        ILogger logger)
    {
        var source = declarations ?? [];
        return Normalize(source, endpoint, logger, tool => tool.ToFunctionTool(), Describe);
    }

    public static List<OpenAiFunctionTool> Normalize(
        IReadOnlyList<OpenAiResponsesTool>? declarations,
        string endpoint,
        ILogger logger)
    {
        var source = declarations ?? [];
        return Normalize(source, endpoint, logger, tool => tool.ToCallableTool(), Describe);
    }

    public static void LogProviderManifest(string providerName, IReadOnlyList<OpenAiFunctionTool> tools, ILogger logger)
    {
        logger.LogInformation(
            "Klientverktøykatalog sendes til {ProviderName}: {ToolCount} verktøy: {ToolManifest}",
            providerName,
            tools.Count,
            DescribeNormalized(tools));
    }

    private static List<OpenAiFunctionTool> Normalize<T>(
        IReadOnlyList<T> declarations,
        string endpoint,
        ILogger logger,
        Func<T, OpenAiFunctionTool?> toCallableTool,
        Func<T, string> describe)
    {
        logger.LogInformation(
            "Klientverktøykatalog mottatt for {Endpoint}: {ToolCount} deklarasjoner: {ToolManifest}",
            endpoint,
            declarations.Count,
            string.Join(", ", declarations.Select(describe)));

        var accepted = new List<OpenAiFunctionTool>(declarations.Count);
        var ignored = new List<string>();
        foreach (var declaration in declarations)
        {
            var tool = toCallableTool(declaration);
            if (tool == null)
                ignored.Add(describe(declaration));
            else
                accepted.Add(tool);
        }

        logger.LogInformation(
            "Klientverktøykatalog normalisert for {Endpoint}: {ToolCount} verktøy: {ToolManifest}",
            endpoint,
            accepted.Count,
            DescribeNormalized(accepted));

        if (ignored.Count > 0)
        {
            logger.LogWarning(
                "Ignorerte {IgnoredToolCount} ugyldige eller ikke-støttede klientverktøy for {Endpoint}: {IgnoredTools}",
                ignored.Count,
                endpoint,
                string.Join(", ", ignored));
        }

        return accepted;
    }

    private static string Describe(OpenAiChatTool tool) =>
        $"{tool.Type}:{tool.Function?.Name ?? tool.Custom?.Name ?? "<missing name>"}";

    private static string Describe(OpenAiResponsesTool tool) =>
        $"{tool.Type}:{tool.NameOrNestedName ?? "<missing name>"}";

    private static string DescribeNormalized(IEnumerable<OpenAiFunctionTool> tools) =>
        string.Join(", ", tools.Select(tool => $"{tool.Type}:{tool.Name}"));
}
