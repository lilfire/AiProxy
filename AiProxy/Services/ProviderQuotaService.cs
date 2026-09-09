using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Contracts;

namespace AiProxy.Services;

/// <summary>
/// Reads authenticated subscription quotas from the locally installed provider CLIs.
/// Values are cached briefly so opening the administration UI does not repeatedly call an account API.
/// </summary>
public sealed class ProviderQuotaService : IProviderQuotaService
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(10);
    // Codex starts an app-server before it can query the account API. Allow for a cold CLI start.
    private const int CodexQuotaTimeoutSeconds = 30;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ILogger<ProviderQuotaService> _logger;
    private readonly ConcurrentDictionary<string, CachedQuota> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ProviderQuotaService(IHttpClientFactory httpClientFactory, ShellCommandRunner commandRunner, ILogger<ProviderQuotaService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _commandRunner = commandRunner;
        _logger = logger;
    }

    public ProviderQuotaSnapshot? GetCachedSnapshot(string providerName)
    {
        if (!SupportsQuota(providerName))
            return null;

        return _cache.TryGetValue(providerName, out var cached) ? cached.Snapshot : null;
    }

    public async Task<ProviderQuotaSnapshot?> GetSnapshotAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (!SupportsQuota(providerName))
            return null;

        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(providerName, out var cached) && cached.ExpiresAt > now)
            return cached.Snapshot;

        var gate = _locks.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(providerName, out cached) && cached.ExpiresAt > now)
                return cached.Snapshot;

            var snapshot = providerName switch
            {
                var name when name.Equals(OpenAiConstants.Providers.Claude, StringComparison.OrdinalIgnoreCase) => await ReadClaudeAsync(cancellationToken),
                var name when name.Equals(OpenAiConstants.Providers.Codex, StringComparison.OrdinalIgnoreCase) => await ReadCodexAsync(cancellationToken),
                var name when name.Equals(OpenAiConstants.Providers.Aigravity, StringComparison.OrdinalIgnoreCase) => await ReadAigravityAsync(cancellationToken),
                _ => await ReadGrokAsync(cancellationToken)
            };
            var duration = snapshot.Error == null ? SuccessCacheDuration : FailureCacheDuration;
            _cache[providerName] = new CachedQuota(snapshot, now.Add(duration));
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProviderQuotaSnapshot> ReadClaudeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var credentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(credentialsPath))
                return Unavailable("Claude er ikke innlogget lokalt.");

            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath, cancellationToken));
            if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenElement) ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
                return Unavailable("Claude OAuth-innlogging mangler.");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("AiProxy/1.0");

            var client = _httpClientFactory.CreateClient(nameof(ProviderQuotaService));
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Unavailable($"Claude-kvoten kunne ikke leses ({(int)response.StatusCode}).");

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var windows = new List<ProviderQuotaWindow>();
            AddClaudeWindow(payload.RootElement, windows, "five_hour", "5-timersøkt", 300);
            AddClaudeWindow(payload.RootElement, windows, "seven_day", "7-dagersgrense", 10_080);
            return new ProviderQuotaSnapshot(windows, DateTimeOffset.UtcNow, windows.Count == 0 ? "Claude returnerte ingen aktive kvotevinduer." : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Kunne ikke lese Claude-abonnementskvote");
            return Unavailable("Claude-kvoten kunne ikke leses.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("Claude-kvoten svarte ikke i tide.");
        }
    }

    private async Task<ProviderQuotaSnapshot> ReadCodexAsync(CancellationToken cancellationToken)
    {
        try
        {
            const long initializeId = 1;
            const long quotaId = 2;
            var lines = new[]
            {
                JsonSerializer.Serialize(new { id = initializeId, method = "initialize", @params = new
                {
                    clientInfo = new { name = "AiProxy", title = "AiProxy", version = "1.0" },
                    capabilities = new { experimentalApi = false, requestAttestation = false }
                }}),
                JsonSerializer.Serialize(new { method = "initialized", @params = new { } }),
                JsonSerializer.Serialize(new { id = quotaId, method = "account/rateLimits/read" })
            };
            var result = await _commandRunner.RunJsonRpcAsync("codex", ["app-server", "--stdio"], lines, quotaId, CodexQuotaTimeoutSeconds, initializeId, cancellationToken);
            using var payload = JsonDocument.Parse(result);
            if (!payload.RootElement.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
                return Unavailable("Codex returnerte ingen kontokvote.");

            var windows = new List<ProviderQuotaWindow>();
            AddCodexWindow(limits, windows, "primary");
            AddCodexWindow(limits, windows, "secondary");
            return new ProviderQuotaSnapshot(windows, DateTimeOffset.UtcNow, windows.Count == 0 ? "Codex returnerte ingen aktive kvotevinduer." : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Kunne ikke lese Codex-abonnementskvote");
            return Unavailable("Codex-kvoten kunne ikke leses.");
        }
    }

    private async Task<ProviderQuotaSnapshot> ReadAigravityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _commandRunner.RunCommandAsync("agy", ["--print", "/usage", "--output-format", "json"], timeoutSeconds: 12, cancellationToken: cancellationToken);
            using var payload = JsonDocument.Parse(output);
            if (!payload.RootElement.TryGetProperty("command", out var command) ||
                !command.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return Unavailable("Aigravity returnerte ingen kvoteinformasjon.");

            var windows = new List<ProviderQuotaWindow>();
            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("name", out var groupNameElement) ? groupNameElement.GetString() : null;
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (!bucket.TryGetProperty("remaining_fraction", out var remaining) || !remaining.TryGetDouble(out var remainingFraction))
                        continue;

                    var window = bucket.TryGetProperty("window", out var windowElement) ? windowElement.GetString() : null;
                    var bucketName = bucket.TryGetProperty("name", out var bucketNameElement) ? bucketNameElement.GetString() : null;
                    var name = string.IsNullOrWhiteSpace(groupName) ? bucketName ?? "Aigravity-kvote" : $"{groupName} · {FormatAigravityWindow(window, bucketName)}";
                    windows.Add(new ProviderQuotaWindow(name, ToPercent((1 - remainingFraction) * 100), GetDateTimeOffset(bucket, "reset_time"), WindowDurationMinutes(window)));
                }
            }

            return new ProviderQuotaSnapshot(windows, DateTimeOffset.UtcNow, windows.Count == 0 ? "Aigravity returnerte ingen aktive kvotevinduer." : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Kunne ikke lese Aigravity-abonnementskvote");
            return Unavailable("Aigravity-kvoten kunne ikke leses.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("Aigravity-kvoten svarte ikke i tide.");
        }
    }

    private async Task<ProviderQuotaSnapshot> ReadGrokAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentialsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "auth.json");
            if (!File.Exists(credentialsPath))
                return Unavailable("Grok er ikke innlogget lokalt.");

            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath, cancellationToken));
            var account = credentials.RootElement.EnumerateObject().Select(property => property.Value).FirstOrDefault(value =>
                value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("key", out var token) && !string.IsNullOrWhiteSpace(token.GetString()) &&
                value.TryGetProperty("user_id", out var userId) && !string.IsNullOrWhiteSpace(userId.GetString()));
            if (account.ValueKind != JsonValueKind.Object ||
                !account.TryGetProperty("key", out var tokenElement) ||
                !account.TryGetProperty("user_id", out var userIdElement))
                return Unavailable("Grok-innlogging mangler.");

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://cli-chat-proxy.grok.com/v1/billing?format=credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            request.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
            request.Headers.Add("x-userid", userIdElement.GetString());
            request.Headers.Add("x-grok-client-version", "0.2.118");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var client = _httpClientFactory.CreateClient(nameof(ProviderQuotaService));
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Unavailable($"Grok-kvoten kunne ikke leses ({(int)response.StatusCode}).");

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            if (!payload.RootElement.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
                return Unavailable("Grok returnerte ingen kvoteinformasjon.");

            var usedPercent = GetGrokUsedPercent(config);
            var currentPeriod = config.TryGetProperty("currentPeriod", out var period) && period.ValueKind == JsonValueKind.Object ? period : default;
            var periodType = currentPeriod.ValueKind == JsonValueKind.Object && currentPeriod.TryGetProperty("type", out var type) ? type.GetString() : null;
            var reset = currentPeriod.ValueKind == JsonValueKind.Object ? GetDateTimeOffset(currentPeriod, "end") : GetDateTimeOffset(config, "billingPeriodEnd");
            return new ProviderQuotaSnapshot([new ProviderQuotaWindow(FormatGrokWindow(periodType), usedPercent, reset, WindowDurationMinutes(periodType))], DateTimeOffset.UtcNow, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Kunne ikke lese Grok-abonnementskvote");
            return Unavailable("Grok-kvoten kunne ikke leses.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("Grok-kvoten svarte ikke i tide.");
        }
    }

    private static void AddClaudeWindow(JsonElement root, ICollection<ProviderQuotaWindow> windows, string propertyName, string name, int durationMinutes)
    {
        if (!root.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var utilization) || !utilization.TryGetDouble(out var used))
            return;

        windows.Add(new ProviderQuotaWindow(name, ToPercent(used), GetDateTimeOffset(window, "resets_at"), durationMinutes));
    }

    private static void AddCodexWindow(JsonElement limits, ICollection<ProviderQuotaWindow> windows, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var used) || !used.TryGetInt32(out var usedPercent))
            return;

        int? duration = window.TryGetProperty("windowDurationMins", out var durationElement) && durationElement.TryGetInt32(out var value) ? value : null;
        DateTimeOffset? reset = window.TryGetProperty("resetsAt", out var resetElement) && resetElement.TryGetInt64(out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
        windows.Add(new ProviderQuotaWindow(FormatWindowName(duration, windows.Count), Math.Clamp(usedPercent, 0, 100), reset, duration));
    }

    private static string FormatWindowName(int? durationMinutes, int index) => durationMinutes switch
    {
        300 => "5-timersøkt",
        { } minutes when minutes % 1_440 == 0 => $"{minutes / 1_440}-dagersgrense",
        { } minutes when minutes % 60 == 0 => $"{minutes / 60}-timersøkt",
        { } minutes => $"{minutes}-minuttersgrense",
        _ => index == 0 ? "Sesjonsgrense" : "Sekundær grense"
    };

    private static int GetGrokUsedPercent(JsonElement config)
    {
        if (config.TryGetProperty("creditUsagePercent", out var usage) && usage.TryGetDouble(out var percentage))
            return ToPercent(percentage);

        if (config.TryGetProperty("used", out var used) && used.TryGetProperty("val", out var usedValue) && usedValue.TryGetDouble(out var usedAmount) &&
            config.TryGetProperty("monthlyLimit", out var limit) && limit.TryGetProperty("val", out var limitValue) && limitValue.TryGetDouble(out var limitAmount) && limitAmount > 0)
            return ToPercent(usedAmount / limitAmount * 100);

        // The Grok billing API omits zero-valued proto3 fields. A present period with no usage amount is therefore 0 % used.
        return 0;
    }

    private static string FormatAigravityWindow(string? window, string? bucketName) => window?.Equals("weekly", StringComparison.OrdinalIgnoreCase) == true ? "ukesgrense" : bucketName ?? "kvote";
    private static string FormatGrokWindow(string? periodType) => periodType?.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase) == true ? "Ukeskvote" : periodType?.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase) == true ? "Månedskvote" : "Grok-kvote";
    private static int? WindowDurationMinutes(string? periodType) => periodType?.Contains("WEEKLY", StringComparison.OrdinalIgnoreCase) == true || periodType?.Equals("weekly", StringComparison.OrdinalIgnoreCase) == true ? 10_080 : periodType?.Contains("MONTHLY", StringComparison.OrdinalIgnoreCase) == true ? 43_200 : null;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var reset) && reset.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(reset.GetString(), out var parsed)
            ? parsed
            : null;

    private static int ToPercent(double value) => Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
    private static bool SupportsQuota(string providerName) => providerName.Equals(OpenAiConstants.Providers.Claude, StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals(OpenAiConstants.Providers.Codex, StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals(OpenAiConstants.Providers.Aigravity, StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals(OpenAiConstants.Providers.Grok, StringComparison.OrdinalIgnoreCase);
    private static ProviderQuotaSnapshot Unavailable(string error) => new([], DateTimeOffset.UtcNow, error);
    private sealed record CachedQuota(ProviderQuotaSnapshot Snapshot, DateTimeOffset ExpiresAt);
}
