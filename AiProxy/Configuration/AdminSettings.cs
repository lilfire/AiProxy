namespace AiProxy.Configuration;

/// <summary>Persisted overrides owned by the local administration UI.</summary>
public sealed class AdminSettings
{
    public int TimeoutSeconds { get; set; } = 300;
    public int CacheTtlSeconds { get; set; } = 60;
    public bool TodoBridgeEnabled { get; set; } = true;
    public Dictionary<string, bool> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AdminModelSetting> Models { get; set; } = [];
    public M365CopilotOptions M365Copilot { get; set; } = new();
}

public sealed class AdminModelSetting
{
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
