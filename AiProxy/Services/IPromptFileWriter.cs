namespace AiProxy.Services;

public interface IPromptFileWriter
{
    Task<string> WritePromptFileAsync(
        string sessionId,
        string providerPrefix,
        string prompt,
        CancellationToken cancellationToken = default);

    void TryDelete(string filePath);
}
