using System.Text;
using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ExceptionHandlingMiddlewareTests
{
    [TestMethod]
    [DataRow("/v1/responses", "\"type\":\"error\"")]
    [DataRow("/v1/chat/completions", "\"error\":{")]
    public async Task InvokeAsync_m365_failure_after_stream_start_emits_client_error(string path, string expectedShape)
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new M365CopilotUpstreamException("InternalError", "request-test"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Request.Path = path;
        context.Response.ContentType = "text/event-stream";
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await middleware.InvokeAsync(context);

        var body = Encoding.UTF8.GetString(responseBody.ToArray());
        StringAssert.Contains(body, "event: error\n");
        StringAssert.Contains(body, expectedShape);
        StringAssert.Contains(body, "m365_upstream_error");
        Assert.IsFalse(body.Contains("response.completed", StringComparison.Ordinal));
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [TestMethod]
    public async Task InvokeAsync_m365_upstream_failure_returns_bad_gateway_with_diagnostic_code()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new M365CopilotUpstreamException("InternalError", "request-test", "upstream diagnostic detail"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        var json = Encoding.UTF8.GetString(responseBody.ToArray());
        StringAssert.Contains(json, "m365_upstream_error");
        StringAssert.Contains(json, "InternalError");
        Assert.IsFalse(json.Contains("upstream diagnostic detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InvokeAsync_unknown_model_returns_bad_request()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new UnknownModelException("Modellen 'unknown' ble ikke funnet."),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IErrorResponseFactory, ErrorResponseFactory>()
                .BuildServiceProvider()
        };
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.AreEqual("application/json; charset=utf-8", context.Response.ContentType);
        Assert.IsTrue(Encoding.UTF8.GetString(responseBody.ToArray()).Contains("model_not_found", StringComparison.Ordinal));
    }
}
