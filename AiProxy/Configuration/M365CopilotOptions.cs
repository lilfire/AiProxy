namespace AiProxy.Configuration;

public sealed class M365CopilotOptions
{
    public bool Enabled { get; set; } = true;
    public bool EnableInteractiveLogin { get; set; }
    public string CacheDirectory { get; set; } = string.Empty;
    public string Locale { get; set; } = "en-GB";
    public string TimeZone { get; set; } = "Europe/Oslo";
    /// <summary>Optional workspace root. An empty value uses the application's content root.</summary>
    public string WorkspaceDirectory { get; set; } = string.Empty;
}
