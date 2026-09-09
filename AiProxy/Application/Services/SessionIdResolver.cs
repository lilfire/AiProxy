using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class SessionIdResolver : ISessionIdResolver
{
    private readonly ILogger<SessionIdResolver> _logger;

    public SessionIdResolver(ILogger<SessionIdResolver> logger)
    {
        _logger = logger;
    }

    public string ResolveSessionId(HttpContext context)
    {
        var sessionId = GetHeaderValue(context, OpenAiConstants.SessionIdHeader)
            ?? GetHeaderValue(context, OpenAiConstants.OpenCodeSessionIdHeader)
            ?? GetHeaderValue(context, OpenAiConstants.OpenCodeSessionAffinityHeader);

        if (sessionId != null)
            return sessionId;

        var ephemeralSessionId = "request-" + Guid.NewGuid().ToString("N");
        _logger.LogWarning("Ingen sesjonsheader, bruker isolert sesjon {Session}", ephemeralSessionId);
        return ephemeralSessionId;
    }

    private static string? GetHeaderValue(HttpContext context, string headerName)
    {
        var value = context.Request.Headers[headerName].FirstOrDefault();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
