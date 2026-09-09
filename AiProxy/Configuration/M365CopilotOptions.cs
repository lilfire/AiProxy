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
    /// <summary>Automatically include a bounded, secret-filtered workspace snapshot in M365 requests.</summary>
    public bool IncludeWorkspaceFiles { get; set; } = true;
    /// <summary>Apply complete-file blocks returned for files in the workspace snapshot.</summary>
    public bool EnableWorkspaceWrites { get; set; } = true;
}
