using Microsoft.Extensions.Logging;

namespace AiProxy.Services.Logging;

public sealed class InMemoryLoggerProvider(ILogStore logStore) : ILoggerProvider
{
    private long _nextSequence;

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, logStore, this);

    public void Dispose()
    {
    }

    private sealed class InMemoryLogger(string category, ILogStore logStore, InMemoryLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var sequence = Interlocked.Increment(ref provider._nextSequence);
            logStore.Add(new LogEntry(sequence, DateTimeOffset.Now, logLevel.ToString(), category, message, exception?.ToString()));
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }
}
