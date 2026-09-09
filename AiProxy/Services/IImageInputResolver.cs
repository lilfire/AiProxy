using AiProxy.Contracts;

namespace AiProxy.Services;

public interface IImageInputResolver
{
    Task<ResolvedImages> ResolveAsync(IEnumerable<OpenAiImageInput> inputs, CancellationToken cancellationToken = default);
}

public sealed class ResolvedImages : IAsyncDisposable
{
    private readonly IReadOnlyList<string> _paths;

    public ResolvedImages(IReadOnlyList<string> paths) => _paths = paths;
    public IReadOnlyList<string> Paths => _paths;

    public ValueTask DisposeAsync()
    {
        foreach (var path in _paths)
        {
            try { File.Delete(path); }
            catch (Exception) { }
        }
        return ValueTask.CompletedTask;
    }
}
