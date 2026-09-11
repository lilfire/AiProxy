using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Routes;
using AiProxy.Services;
using AiProxy.Services.Logging;
using AiProxy.Services.Sessions;
using AiProxy.Services.Providers;
using AiProxy.Validators;
using FluentValidation;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var aiProxyOptions = builder.Configuration.GetSection("AiProxy").Get<AiProxyOptions>() ?? new AiProxyOptions();

builder.Logging.ClearProviders();
builder.Logging.AddDebug();

builder.Services.Configure<AiProxyOptions>(builder.Configuration.GetSection("AiProxy"));
builder.Services.AddSingleton<IRuntimeSettings>(_ => new RuntimeSettings(aiProxyOptions, builder.Environment));
builder.Services.AddSingleton<ILogStore, InMemoryLogStore>();
builder.Services.AddSingleton<ISessionHistoryStore, InMemorySessionHistoryStore>();
builder.Services.AddSingleton<InMemoryLoggerProvider>();
builder.Logging.Services.AddSingleton<ILoggerProvider>(serviceProvider => serviceProvider.GetRequiredService<InMemoryLoggerProvider>());

builder.Services.AddSingleton<IExecutablePathResolver, ExecutablePathResolver>();
builder.Services.AddSingleton<ShellCommandRunner>();
builder.Services.AddSingleton<ProviderSessionStore>();
builder.Services.AddSingleton<IProviderUsageStore, ProviderUsageStore>();
builder.Services.AddSingleton<IUsageUpdateNotifier>(sp => (IUsageUpdateNotifier)sp.GetRequiredService<IProviderUsageStore>());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IProviderQuotaService, ProviderQuotaService>();
builder.Services.AddSingleton<IQuotaUpdateNotifier>(sp => (IQuotaUpdateNotifier)sp.GetRequiredService<IProviderQuotaService>());
builder.Services.AddHostedService<ProviderQuotaRefreshService>();
builder.Services.AddSingleton<M365CopilotSessionStore>();
builder.Services.AddSingleton<M365CopilotTokenProvider>();
builder.Services.AddSingleton<IM365CopilotBrowserClient, M365CopilotBrowserClient>();
builder.Services.AddSingleton<M365CopilotWorkspaceBridge>();
builder.Services.AddSingleton<IModelIdCache, ModelIdCache>();
builder.Services.AddSingleton<IPromptFileWriter, PromptFileWriter>();
builder.Services.AddSingleton<IImageInputResolver, ImageInputResolver>();

// Må være Scoped: et delt lager ville lekket todo-lister mellom samtidige forespørsler.
builder.Services.AddScoped<ITodoSnapshotStore, TodoSnapshotStore>();

builder.Services.AddScoped<IChatProvider, AigravityProvider>();
builder.Services.AddScoped<IChatProvider, GrokProvider>();
builder.Services.AddScoped<IChatProvider, ClaudeProvider>();
builder.Services.AddScoped<IChatProvider, CodexProvider>();
builder.Services.AddScoped<IChatProvider, M365CopilotProvider>();

builder.Services.AddScoped<IChatProviderRegistry, ChatProviderRegistry>();
builder.Services.AddScoped<IAdminService, AdminService>();

builder.Services.AddScoped<IModelService, ModelService>();
builder.Services.AddScoped<IChatRequestFactory, ChatRequestFactory>();
builder.Services.AddScoped<IChatCompletionService, ChatCompletionService>();
builder.Services.AddScoped<IResponsesService, ResponsesService>();
builder.Services.AddScoped<IResponsesRequestMapper, ResponsesRequestMapper>();
builder.Services.AddScoped<ISessionIdResolver, SessionIdResolver>();
builder.Services.AddScoped<ITodoItemMapper, TodoItemMapper>();
builder.Services.AddScoped<ITodoToolBridge, TodoToolBridge>();
builder.Services.AddScoped<TodoOutputProtocol>();
builder.Services.AddScoped<IErrorResponseFactory, ErrorResponseFactory>();
builder.Services.AddScoped<IOpenAiStreamFormatter, OpenAiStreamFormatter>();
builder.Services.AddScoped<IOpenAiResponseEventBuilder, OpenAiResponseEventBuilder>();

builder.Services.AddValidatorsFromAssemblyContaining<OpenAiChatRequestValidator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRazorPages();
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = aiProxyOptions.AllowedOrigins;

        if (origins.Length > 0)
        {
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/admin"), admin => admin.Use(async (context, next) =>
{
    var address = context.Connection.RemoteIpAddress;
    if (address == null || !System.Net.IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Administrasjonssiden er kun tilgjengelig lokalt.");
        return;
    }

    await next();
}));
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.MapAiProxyRoutes();
app.MapAdminRoutes();
app.MapRazorPages();

app.Run($"http://{aiProxyOptions.Host}:{aiProxyOptions.Port}");
