using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Services;
using AiProxy.Services.Logging;
using AiProxy.Services.Sessions;
using System.Text.Json;
using System.Threading.Channels;

namespace AiProxy.Routes;

public static class AdminRoute
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAdminRoutes(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin/api");
        admin.MapGet("/settings", (IAdminService service) => Results.Ok(service.GetSettings()));
        admin.MapPut("/settings", async (AdminSettings settings, IAdminService service, CancellationToken ct) =>
        {
            await service.SaveSettingsAsync(settings, ct);
            return Results.NoContent();
        });
        admin.MapGet("/providers", async (IAdminService service, CancellationToken ct) => Results.Ok(await service.GetProviderStatusesAsync(ct)));
        admin.MapPost("/models/refresh", async (IAdminService service, CancellationToken ct) =>
        {
            await service.RefreshModelsAsync(ct);
            return Results.NoContent();
        });
        admin.MapGet("/m365/status", async (M365CopilotTokenProvider tokenProvider, CancellationToken ct) => Results.Ok(await tokenProvider.GetStatusAsync(ct)));
        admin.MapGet("/logs/stream", StreamLogsAsync);
        admin.MapGet("/sessions", (ISessionHistoryStore sessionHistory) => Results.Ok(sessionHistory.GetSnapshot()));
        admin.MapGet("/sessions/stream", StreamSessionsAsync);
        admin.MapGet("/quota/stream", StreamQuotaAsync);
        admin.MapGet("/usage/stream", StreamUsageAsync);
        return app;
    }

    private static async Task StreamLogsAsync(HttpContext context, ILogStore logStore, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var subscription = logStore.Subscribe(entry => channel.Writer.TryWrite(entry));
        var snapshot = logStore.GetSnapshot();
        var lastSequence = 0L;

        try
        {
            foreach (var entry in snapshot)
            {
                await WriteLogEventAsync(context, entry, cancellationToken);
                lastSequence = entry.Sequence;
            }

            await context.Response.Body.FlushAsync(cancellationToken);

            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (entry.Sequence <= lastSequence)
                    continue;

                await WriteLogEventAsync(context, entry, cancellationToken);
                lastSequence = entry.Sequence;
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nettleseren lukket loggstrømmen.
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private static Task WriteLogEventAsync(HttpContext context, LogEntry entry, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        return context.Response.WriteAsync($"event: log\ndata: {json}\n\n", cancellationToken);
    }

    private static async Task StreamSessionsAsync(HttpContext context, ISessionHistoryStore sessionHistory, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var channel = Channel.CreateUnbounded<SessionHistoryChanged>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        using var subscription = sessionHistory.Subscribe(change => channel.Writer.TryWrite(change));
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(change, JsonOptions);
                await context.Response.WriteAsync($"event: session\ndata: {json}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nettleseren lukket sesjonsstrømmen.
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private static async Task StreamQuotaAsync(HttpContext context, IQuotaUpdateNotifier notifier, IProviderQuotaService quotaService, IAdminService adminService, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var channel = Channel.CreateUnbounded<QuotaUpdateEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        using var subscription = notifier.Subscribe((providerName, snapshot) =>
            channel.Writer.TryWrite(new QuotaUpdateEvent(providerName, snapshot)));

        try
        {
            var statuses = await adminService.GetProviderStatusesAsync(cancellationToken);
            foreach (var status in statuses)
            {
                if (status.Quota is { } quota)
                {
                    var evt = new QuotaUpdateEvent(status.Name, quota);
                    var json = JsonSerializer.Serialize(evt, JsonOptions);
                    await context.Response.WriteAsync($"event: quota\ndata: {json}\n\n", cancellationToken);
                }
            }
            await context.Response.Body.FlushAsync(cancellationToken);

            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await context.Response.WriteAsync($"event: quota\ndata: {json}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nettleseren lukket kvotestrømmen.
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed record QuotaUpdateEvent(string ProviderName, ProviderQuotaSnapshot Quota);

    private static async Task StreamUsageAsync(HttpContext context, IUsageUpdateNotifier notifier, IProviderUsageStore usageStore, IAdminService adminService, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        var channel = Channel.CreateUnbounded<UsageUpdateEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        using var subscription = notifier.Subscribe((providerName, snapshot) =>
            channel.Writer.TryWrite(new UsageUpdateEvent(providerName, snapshot)));

        try
        {
            var statuses = await adminService.GetProviderStatusesAsync(cancellationToken);
            foreach (var status in statuses)
            {
                var evt = new UsageUpdateEvent(status.Name, status.Usage);
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await context.Response.WriteAsync($"event: usage\ndata: {json}\n\n", cancellationToken);
            }
            await context.Response.Body.FlushAsync(cancellationToken);

            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await context.Response.WriteAsync($"event: usage\ndata: {json}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nettleseren lukket bruksstrømmen.
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed record UsageUpdateEvent(string ProviderName, ProviderUsageSnapshot Usage);
}
