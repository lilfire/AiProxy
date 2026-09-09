using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Contracts;

namespace AiProxy.Services;

/// <summary>
/// Refreshes optional subscription quotas away from request handling. A stalled provider CLI must
/// never delay the administration UI or the proxy's request path.
/// </summary>
public sealed class ProviderQuotaRefreshService : BackgroundService
{
    private static readonly string[] ProviderNames =
    [
        OpenAiConstants.Providers.Aigravity,
        OpenAiConstants.Providers.Grok,
        OpenAiConstants.Providers.Claude,
        OpenAiConstants.Providers.Codex
    ];

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private readonly IProviderQuotaService _quotaService;
    private readonly IRuntimeSettings _settings;
    private readonly ILogger<ProviderQuotaRefreshService> _logger;

    public ProviderQuotaRefreshService(
        IProviderQuotaService quotaService,
        IRuntimeSettings settings,
        ILogger<ProviderQuotaRefreshService> logger)
    {
        _quotaService = quotaService;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var providers = ProviderNames.Where(IsEnabled).ToArray();
                try
                {
                    await Task.WhenAll(providers.Select(provider => _quotaService.GetSnapshotAsync(provider, stoppingToken)));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Kunne ikke oppdatere en eller flere abonnementskvoter i bakgrunnen");
                }

                await Task.Delay(RefreshInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private bool IsEnabled(string providerName) =>
        !_settings.Current.Providers.TryGetValue(providerName, out var enabled) || enabled;
}
