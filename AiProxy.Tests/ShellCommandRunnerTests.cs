using AiProxy.Configuration;
using AiProxy.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

[TestClass]
public class ShellCommandRunnerTests
{
    private readonly int[] _handlerDelaysMs = [60, 30, 0];

    [TestMethod]
    public async Task Run_command_streaming_keeps_output_line_order_when_handler_is_slow()
    {
        var runner = CreateRunner();
        var handledLines = new List<string>();
        var delays = new Queue<int>(_handlerDelaysMs);

        await runner.RunCommandStreamingAsync(
            ResolveShell(),
            BuildEchoArguments(),
            async (line, _) =>
            {
                await Task.Delay(delays.Count > 0 ? delays.Dequeue() : 0);
                handledLines.Add(line.Trim());
            });

        CollectionAssert.AreEqual(new[] { "en", "to", "tre" }, handledLines);
    }

    [TestMethod]
    public async Task Run_command_with_stderr_and_success_logs_stderr_as_debug()
    {
        var logger = new CapturingLogger<ShellCommandRunner>();
        var runner = CreateRunner(logger);

        var output = await runner.RunCommandAsync(ResolveShell(), BuildSuccessWithStderrArguments());

        Assert.AreEqual("output", output.Trim());
        CollectionAssert.Contains(logger.LogLevels, LogLevel.Debug);
        CollectionAssert.DoesNotContain(logger.LogLevels, LogLevel.Error);
    }

    [TestMethod]
    public async Task Run_command_with_stderr_and_failure_logs_error_and_throws()
    {
        var logger = new CapturingLogger<ShellCommandRunner>();
        var runner = CreateRunner(logger);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => runner.RunCommandAsync(ResolveShell(), BuildFailureWithStderrArguments()));

        CollectionAssert.Contains(logger.LogLevels, LogLevel.Error);
    }

    private string ResolveShell()
    {
        return OperatingSystem.IsWindows() ? "cmd" : "sh";
    }

    private List<string> BuildEchoArguments()
    {
        if (OperatingSystem.IsWindows())
            return ["/c", "echo en&echo to&echo tre"];

        return ["-c", """printf 'en\nto\ntre\n'"""];
    }

    private List<string> BuildSuccessWithStderrArguments()
    {
        if (OperatingSystem.IsWindows())
            return ["/c", "echo output&echo diagnostic 1>&2"];

        return ["-c", "printf 'output\\n'; printf 'diagnostic\\n' >&2"];
    }

    private List<string> BuildFailureWithStderrArguments()
    {
        if (OperatingSystem.IsWindows())
            return ["/c", "echo diagnostic 1>&2&exit /b 7"];

        return ["-c", "printf 'diagnostic\\n' >&2; exit 7"];
    }

    private ShellCommandRunner CreateRunner(ILogger<ShellCommandRunner>? logger = null)
    {
        return new ShellCommandRunner(new ExecutablePathResolver(), logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> LogLevels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => LogLevels.Add(logLevel);
    }
}
