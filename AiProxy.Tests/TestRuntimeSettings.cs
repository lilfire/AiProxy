using AiProxy.Configuration;

namespace AiProxy.Tests;

internal sealed class TestRuntimeSettings : IRuntimeSettings
{
    public TestRuntimeSettings(AiProxyOptions? options = null)
    {
        options ??= new AiProxyOptions();
        Current = new AdminSettings
        {
            TimeoutSeconds = options.TimeoutSeconds,
            CacheTtlSeconds = options.CacheTtlSeconds,
            TodoBridgeEnabled = options.TodoBridgeEnabled
        };
    }

    public AdminSettings Current { get; private set; }

    public Task SaveAsync(AdminSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}
