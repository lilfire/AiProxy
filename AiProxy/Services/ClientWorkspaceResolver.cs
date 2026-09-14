using Microsoft.AspNetCore.Http;

namespace AiProxy.Services;

/// <summary>Reads the active OpenCode project directory from a request header.</summary>
public static class ClientWorkspaceResolver
{
    public const string HeaderName = "X-ShellAi-Workspace";

    public static string? Resolve(HttpRequest request)
    {
        var candidate = request.Headers[HeaderName].ToString().Trim();

        if (string.IsNullOrWhiteSpace(candidate) ||
            !Path.IsPathFullyQualified(candidate) ||
            !Directory.Exists(candidate))
            return null;

        return Path.GetFullPath(candidate);
    }
}
