namespace AiProxy.Configuration;

public class AiProxyOptions
{
    private const int DefaultCacheTtlSeconds = 60;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3456;
    public int TimeoutSeconds { get; set; } = 300;
    public int CacheTtlSeconds { get; set; } = DefaultCacheTtlSeconds;
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Slår av videresending av TodoWrite-kall som verktøykall til OpenCode-klienten.</summary>
    public bool TodoBridgeEnabled { get; set; } = true;

    public M365CopilotOptions M365Copilot { get; set; } = new();
}
