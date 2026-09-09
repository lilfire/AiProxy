namespace AiProxy.Services;

public sealed class PromptFileWriter : IPromptFileWriter
{
    public async Task<string> WritePromptFileAsync(
        string sessionId,
        string providerPrefix,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var promptFileId = $"{sessionId}-{Guid.NewGuid():N}";
        var promptFilePath = Path.Combine(Path.GetTempPath(), $"aiproxy-{providerPrefix}-prompt-{promptFileId}.txt");

        await File.WriteAllTextAsync(promptFilePath, prompt, cancellationToken);

        return promptFilePath;
    }

    public void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
    }
}
