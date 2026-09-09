using System.Text;
using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ExceptionHandlingMiddlewareTests
{
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
