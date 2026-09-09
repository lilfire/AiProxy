using System.Text.Json;
using AiProxy.Contracts;

namespace AiProxy.Configuration;

public sealed class RuntimeSettings : IRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private AdminSettings _current;

    public RuntimeSettings(AiProxyOptions defaults, IHostEnvironment environment)
    {
        _settingsPath = Path.Combine(environment.ContentRootPath, "aiproxy.admin.json");
        _current = Load(defaults);
    }

    public AdminSettings Current => Volatile.Read(ref _current);

    public async Task SaveAsync(AdminSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var temporaryPath = _settingsPath + ".tmp";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _settingsPath, true);
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private AdminSettings Load(AiProxyOptions defaults)
    {
        var baseline = CreateBaseline(defaults);
        if (!File.Exists(_settingsPath))
            return baseline;

        try
        {
            var saved = JsonSerializer.Deserialize<AdminSettings>(File.ReadAllText(_settingsPath), JsonOptions);
            return saved == null ? baseline : Normalize(saved, baseline);
        }
        catch (JsonException)
        {
            return baseline;
        }
    }

    private static AdminSettings CreateBaseline(AiProxyOptions options) => new()
    {
        TimeoutSeconds = options.TimeoutSeconds,
        CacheTtlSeconds = options.CacheTtlSeconds,
        TodoBridgeEnabled = options.TodoBridgeEnabled,
        Providers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [OpenAiConstants.Providers.Aigravity] = true,
            [OpenAiConstants.Providers.Grok] = true,
            [OpenAiConstants.Providers.Claude] = true,
            [OpenAiConstants.Providers.Codex] = true,
            [OpenAiConstants.Providers.M365Copilot] = options.M365Copilot.Enabled
        },
        M365Copilot = new M365CopilotOptions
        {
            Enabled = options.M365Copilot.Enabled,
            EnableInteractiveLogin = options.M365Copilot.EnableInteractiveLogin,
            CacheDirectory = options.M365Copilot.CacheDirectory,
            Locale = options.M365Copilot.Locale,
            TimeZone = options.M365Copilot.TimeZone,
            WorkspaceDirectory = options.M365Copilot.WorkspaceDirectory,
            IncludeWorkspaceFiles = options.M365Copilot.IncludeWorkspaceFiles,
            EnableWorkspaceWrites = options.M365Copilot.EnableWorkspaceWrites
        }
    };

    private static AdminSettings Normalize(AdminSettings candidate, AdminSettings? baseline = null)
    {
        baseline ??= new AdminSettings();
        candidate.TimeoutSeconds = Math.Clamp(candidate.TimeoutSeconds, 5, 3600);
        candidate.CacheTtlSeconds = Math.Clamp(candidate.CacheTtlSeconds, 0, 3600);
        candidate.Providers ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in baseline.Providers)
            if (!candidate.Providers.ContainsKey(provider.Key))
                candidate.Providers[provider.Key] = provider.Value;
        candidate.Models ??= [];
        candidate.M365Copilot ??= baseline.M365Copilot;
        candidate.M365Copilot.Locale = string.IsNullOrWhiteSpace(candidate.M365Copilot.Locale) ? "en-GB" : candidate.M365Copilot.Locale.Trim();
        candidate.M365Copilot.TimeZone = string.IsNullOrWhiteSpace(candidate.M365Copilot.TimeZone) ? "Europe/Oslo" : candidate.M365Copilot.TimeZone.Trim();
        candidate.M365Copilot.Enabled = candidate.Providers.TryGetValue(OpenAiConstants.Providers.M365Copilot, out var enabled) && enabled;
        return candidate;
    }
}
