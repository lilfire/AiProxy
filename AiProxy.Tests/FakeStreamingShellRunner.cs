using AiProxy.Configuration;
using AiProxy.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

internal class FakeStreamingShellRunner : ShellCommandRunner
{
    private readonly Queue<string> _outputLines = new();

    public FakeStreamingShellRunner()
        : base(new ExecutablePathResolver(), NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings())
    {
    }

    public void QueueOutputLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            _outputLines.Enqueue(line);
    }

    public override async Task RunCommandStreamingAsync(
        string command,
        IEnumerable<string> argumentSegments,
        Func<string, CancellationToken, Task> onOutputLine,
        string? workingDirectory = null,
        string? stdinInput = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        while (_outputLines.Count > 0)
        {
            await onOutputLine(_outputLines.Dequeue(), cancellationToken);
        }
    }
}
