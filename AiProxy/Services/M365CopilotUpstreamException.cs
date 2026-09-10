namespace AiProxy.Services;

public sealed class M365CopilotUpstreamException(string code, string requestId, string? detail = null)
    : Exception($"M365 Copilot returned {code} (request {requestId}). {detail}".TrimEnd())
{
    public string Code { get; } = code;
    public string RequestId { get; } = requestId;
}
