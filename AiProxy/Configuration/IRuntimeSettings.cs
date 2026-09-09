namespace AiProxy.Configuration;

public interface IRuntimeSettings
{
    AdminSettings Current { get; }
    Task SaveAsync(AdminSettings settings, CancellationToken cancellationToken = default);
}
