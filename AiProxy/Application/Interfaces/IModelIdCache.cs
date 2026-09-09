namespace AiProxy.Application.Interfaces;

public interface IModelIdCache
{
    Task<IReadOnlyList<string>> GetOrAddAsync(string providerName, Func<CancellationToken, Task<IReadOnlyList<string>>> factory, CancellationToken cancellationToken = default);
    void Clear(string? providerName = null);
}
