namespace AiProxy.Services.Logging;

public interface ILogStore
{
    IReadOnlyList<LogEntry> GetSnapshot();
    IDisposable Subscribe(Action<LogEntry> listener);
    void Add(LogEntry entry);
}
