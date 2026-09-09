using AiProxy.Application.Interfaces;
using AiProxy.Contracts;

namespace AiProxy.Application.Services;

public sealed class ErrorResponseFactory : IErrorResponseFactory
{
    public OpenAiErrorResponse CreateRateLimitResponse(string message, int retryAfterSeconds)
    {
        var error = new OpenAiError
        {
            Message = message,
            Type = OpenAiConstants.ErrorType,
            Code = OpenAiConstants.ErrorCodeRateLimit
        };

        return new OpenAiErrorResponse(error);
    }

    public OpenAiErrorResponse CreateInternalServerErrorResponse(string message)
    {
        var error = new OpenAiError
        {
            Message = message,
            Type = OpenAiConstants.ErrorType
        };

        return new OpenAiErrorResponse(error);
    }

    public OpenAiErrorResponse CreateInvalidRequestResponse(string message, string? code = null)
    {
        var error = new OpenAiError
        {
            Message = message,
            Type = "invalid_request_error",
            Code = code
        };

        return new OpenAiErrorResponse(error);
    }
}
