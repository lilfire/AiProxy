using AiProxy.Contracts;

namespace AiProxy.Application.Interfaces;

public interface IErrorResponseFactory
{
    OpenAiErrorResponse CreateRateLimitResponse(string message, int retryAfterSeconds);
    OpenAiErrorResponse CreateInvalidRequestResponse(string message, string? code = null);
    OpenAiErrorResponse CreateInternalServerErrorResponse(string message);
}
