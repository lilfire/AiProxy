using System.Runtime.ExceptionServices;
using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (QuotaExceededException ex)
        {
            await HandleQuotaExceededAsync(context, ex);
        }
        catch (UnknownModelException ex)
        {
            await HandleUnknownModelAsync(context, ex);
        }
        catch (ImageInputNotSupportedException ex)
        {
            await HandleUnprocessableImageAsync(context, ex);
        }
        catch (ImageInputException ex)
        {
            await HandleInvalidImageAsync(context, ex);
        }
        catch (M365CopilotUpstreamException ex)
        {
            await HandleM365UpstreamErrorAsync(context, ex);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleM365UpstreamErrorAsync(HttpContext context, M365CopilotUpstreamException ex)
    {
        _logger.LogWarning(ex, "M365 Copilot-feil {Code}, request {RequestId}", ex.Code, ex.RequestId);
        var response = new OpenAiErrorResponse(new OpenAiError
        {
            Message = $"M365 Copilot returned {ex.Code}. Request ID: {ex.RequestId}.",
            Type = OpenAiConstants.ErrorType,
            Code = "m365_upstream_error"
        });

        if (context.Response.HasStarted)
        {
            if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) != true)
                ExceptionDispatchInfo.Capture(ex).Throw();

            object streamError = context.Request.Path.Value?.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) == true
                ? new { type = "error", code = response.Error.Code, message = response.Error.Message, param = (string?)null }
                : response;
            var json = JsonSerializer.Serialize(streamError, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await context.Response.WriteAsync($"event: error\ndata: {json}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await WriteErrorResponseAsync(context, response);
    }

    private Task HandleQuotaExceededAsync(HttpContext context, QuotaExceededException ex)
    {
        _logger.LogWarning("Kvote overskredet for forespørsel. Nytt forsøk om {RetryAfter} sekunder.", ex.RetryAfterSeconds);

        if (context.Response.HasStarted)
        {
            _logger.LogError(ex, "Kunne ikke skrive kvotefeil fordi responsen allerede er startet.");
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString();

        var factory = context.RequestServices.GetRequiredService<IErrorResponseFactory>();
        var response = factory.CreateRateLimitResponse($"Rate limit exceeded. Retry after {ex.RetryAfterSeconds} seconds.", ex.RetryAfterSeconds);
        return WriteErrorResponseAsync(context, response);
    }

    private Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        _logger.LogError(ex, "Feil under behandling av forespørsel");

        if (context.Response.HasStarted)
        {
            _logger.LogError(ex, "Kunne ikke skrive feilrespons fordi responsen allerede er startet.");
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var factory = context.RequestServices.GetRequiredService<IErrorResponseFactory>();
        var response = factory.CreateInternalServerErrorResponse("An unexpected error occurred.");
        return WriteErrorResponseAsync(context, response);
    }

    private Task HandleUnknownModelAsync(HttpContext context, UnknownModelException ex)
    {
        _logger.LogWarning(ex, "Forespørselen brukte en ukjent modell");

        if (context.Response.HasStarted)
        {
            _logger.LogError(ex, "Kunne ikke skrive modellfeil fordi responsen allerede er startet.");
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        var factory = context.RequestServices.GetRequiredService<IErrorResponseFactory>();
        return WriteErrorResponseAsync(context, factory.CreateInvalidRequestResponse(ex.Message, "model_not_found"));
    }

    private Task HandleInvalidImageAsync(HttpContext context, ImageInputException ex)
    {
        _logger.LogWarning(ex, "Ugyldig bildevedlegg");
        if (context.Response.HasStarted)
            ExceptionDispatchInfo.Capture(ex).Throw();

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        var factory = context.RequestServices.GetRequiredService<IErrorResponseFactory>();
        return WriteErrorResponseAsync(context, factory.CreateInvalidRequestResponse(ex.Message, "invalid_image"));
    }

    private Task HandleUnprocessableImageAsync(HttpContext context, ImageInputNotSupportedException ex)
    {
        _logger.LogWarning(ex, "Provideren støtter ikke bildevedlegget");
        if (context.Response.HasStarted)
            ExceptionDispatchInfo.Capture(ex).Throw();

        context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        var factory = context.RequestServices.GetRequiredService<IErrorResponseFactory>();
        return WriteErrorResponseAsync(context, factory.CreateInvalidRequestResponse(ex.Message, "image_not_supported"));
    }

    private async Task WriteErrorResponseAsync(HttpContext context, OpenAiErrorResponse response)
    {
        await context.Response.WriteAsJsonAsync(response);
    }
}
