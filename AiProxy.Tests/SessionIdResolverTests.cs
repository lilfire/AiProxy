using AiProxy.Application.Services;
using AiProxy.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class SessionIdResolverTests
{
    private readonly SessionIdResolver _resolver = new(NullLogger<SessionIdResolver>.Instance);

    [TestMethod]
    public void ResolveSessionId_returns_shell_ai_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[OpenAiConstants.SessionIdHeader] = "ses-opencode";

        var sessionId = _resolver.ResolveSessionId(context);

        Assert.AreEqual("ses-opencode", sessionId);
    }

    [TestMethod]
    public void ResolveSessionId_returns_compatible_opencode_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[OpenAiConstants.OpenCodeSessionIdHeader] = "ses-opencode";

        var sessionId = _resolver.ResolveSessionId(context);

        Assert.AreEqual("ses-opencode", sessionId);
    }

    [TestMethod]
    public void ResolveSessionId_creates_isolated_ids_when_headers_are_missing()
    {
        var firstSessionId = _resolver.ResolveSessionId(new DefaultHttpContext());
        var secondSessionId = _resolver.ResolveSessionId(new DefaultHttpContext());

        Assert.AreNotEqual(firstSessionId, secondSessionId);
        StringAssert.StartsWith(firstSessionId, "request-");
        StringAssert.StartsWith(secondSessionId, "request-");
    }
}
