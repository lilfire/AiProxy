using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using FluentValidation;

namespace AiProxy.Routes;

public static class ChatCompletionsRoute
{
    public static IEndpointRouteBuilder MapAiProxyRoutes(this IEndpointRouteBuilder app)
    {
        var versionSet = app.NewApiVersionSet()
            .HasApiVersion(new Asp.Versioning.ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var api = app.MapGroup("/v{version:apiVersion}")
            .WithApiVersionSet(versionSet);

        api.MapGet("/models", async (IModelService modelService, CancellationToken cancellationToken) =>
        {
            var models = await modelService.GetModelsAsync(cancellationToken);
            return TypedResults.Ok(new OpenAiModelsResponse { Data = models.ToList() });
        })
        .WithName("GetModels")
        .WithOpenApi();

        api.MapPost("/chat/completions", async (
            OpenAiChatRequest request,
            IValidator<OpenAiChatRequest> validator,
            IChatCompletionService chatCompletionService,
            ISessionIdResolver sessionIdResolver,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var validationResult = await validator.ValidateAsync(request, cancellationToken);

            if (!validationResult.IsValid)
                return Results.ValidationProblem(validationResult.ToDictionary());

            var sessionId = sessionIdResolver.ResolveSessionId(context);
            return await chatCompletionService.ExecuteAsync(request, sessionId, cancellationToken);
        })
        .WithName("CreateChatCompletion")
        .WithOpenApi();

        api.MapPost("/responses", async (
            OpenAiResponsesRequest request,
            IValidator<OpenAiResponsesRequest> validator,
            IResponsesService responsesService,
            ISessionIdResolver sessionIdResolver,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var validationResult = await validator.ValidateAsync(request, cancellationToken);

            if (!validationResult.IsValid)
                return Results.ValidationProblem(validationResult.ToDictionary());

            var sessionId = sessionIdResolver.ResolveSessionId(context);
            return await responsesService.ExecuteAsync(request, sessionId, cancellationToken);
        })
        .WithName("CreateResponse")
        .WithOpenApi();

        app.MapGet("/health", () => TypedResults.Ok(new { Status = "Healthy" }))
            .WithName("Health")
            .WithOpenApi()
            .AllowAnonymous();

        return app;
    }
}
