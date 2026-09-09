using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Configuration;

namespace AiProxy.Services;

public class ShellCommandRunner

{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly IExecutablePathResolver _executablePathResolver;
    private readonly ILogger<ShellCommandRunner> _logger;
    private readonly IRuntimeSettings _settings;

    public ShellCommandRunner(
        IExecutablePathResolver executablePathResolver,
        ILogger<ShellCommandRunner> logger,
        IRuntimeSettings settings)
    {
        _executablePathResolver = executablePathResolver;
        _logger = logger;
        _settings = settings;
    }

    public async Task<string> RunCommandAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        string? stdinInput = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        return await RunCommandAsync(command, new[] { arguments }, workingDirectory, stdinInput, timeoutSeconds, cancellationToken);
    }

    public async Task<string> RunCommandAsync(
        string command,
        IEnumerable<string> argumentSegments,
        string? workingDirectory = null,
        string? stdinInput = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var executablePath = _executablePathResolver.Resolve(command);
        var argumentList = argumentSegments.ToList();
        var actualWorkingDirectory = ResolveWorkingDirectory(workingDirectory);

        _logger.LogInformation("Kjører kommando {Command} {Arguments}", command, string.Join(" ", argumentList));

        using var process = StartProcess(executablePath, argumentList, actualWorkingDirectory, stdinInput != null);
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => AppendIfNotNull(outputBuilder, e.Data);
        process.ErrorDataReceived += (_, e) => AppendIfNotNull(errorBuilder, e.Data);

        if (!process.Start())
            throw new InvalidOperationException($"Klarte ikke å starte prosess: {command}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WriteStdinAsync(process, stdinInput, cancellationToken);

        await WaitForExitAsync(process, timeoutSeconds ?? _settings.Current.TimeoutSeconds, cancellationToken);

        return BuildResultOrThrow(process.ExitCode, command, outputBuilder, errorBuilder);
    }

    public virtual async Task RunCommandStreamingAsync(
        string command,
        IEnumerable<string> argumentSegments,
        Func<string, CancellationToken, Task> onOutputLine,
        string? workingDirectory = null,
        string? stdinInput = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(onOutputLine);

        var executablePath = _executablePathResolver.Resolve(command);
        var argumentList = argumentSegments.ToList();
        var actualWorkingDirectory = ResolveWorkingDirectory(workingDirectory);

        _logger.LogInformation("Strømmer kommando {Command} {Arguments}", command, string.Join(" ", argumentList));

        using var process = StartProcess(executablePath, argumentList, actualWorkingDirectory, stdinInput != null);
        var errorBuilder = new StringBuilder();
        var outputCompletion = new TaskCompletionSource();
        var pendingOutput = Task.CompletedTask;

        // Håndtereren er async void, så den returnerer ved første await. Uten denne lenkingen
        // kan neste stdout-linje skrives til responsstrømmen før den forrige er ferdig.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                pendingOutput = pendingOutput.ContinueWith(_ => outputCompletion.TrySetResult(), TaskScheduler.Default);
                return;
            }

            pendingOutput = ProcessOutputLineAsync(pendingOutput, e.Data, onOutputLine, outputCompletion, cancellationToken);
        };

        process.ErrorDataReceived += (_, e) => AppendIfNotNull(errorBuilder, e.Data);

        if (!process.Start())
            throw new InvalidOperationException($"Klarte ikke å starte prosess: {command}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WriteStdinAsync(process, stdinInput, cancellationToken);

        await WaitForExitWithOutputAsync(process, outputCompletion, timeoutSeconds ?? _settings.Current.TimeoutSeconds, cancellationToken);

        ThrowIfError(process.ExitCode, command, errorBuilder);
    }

    /// <summary>
    /// Sends JSON-RPC messages to a long-running CLI command and returns the result for one request.
    /// The process is stopped after the matching response, so this is suitable for small status reads.
    /// </summary>
    public virtual async Task<string> RunJsonRpcAsync(
        string command,
        IEnumerable<string> argumentSegments,
        IEnumerable<string> requestLines,
        long responseId,
        int timeoutSeconds,
        long? initializeResponseId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var executablePath = _executablePathResolver.Resolve(command);
        var argumentList = argumentSegments.ToList();
        var requests = requestLines.ToList();
        var phase = "oppstart";
        using var process = StartProcess(executablePath, argumentList, ResolveWorkingDirectory(null), redirectStdin: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        _logger.LogInformation("Leser status fra {Command}", command);
        if (!process.Start())
            throw new InvalidOperationException($"Klarte ikke å starte prosess: {command}");

        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var firstRequestIndex = 0;
            if (initializeResponseId.HasValue)
            {
                if (requests.Count == 0)
                    throw new ArgumentException("En initialize-forespørsel må sendes når initializeResponseId er angitt.", nameof(requestLines));

                phase = "initialisering";
                await process.StandardInput.WriteLineAsync(requests[0].AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);

                var initializeResult = await ReadJsonRpcResultAsync(process, command, initializeResponseId.Value, timeout.Token);
                if (initializeResult == null)
                {
                    var initializeStderr = await errorTask;
                    throw new InvalidOperationException($"{command} avsluttet før initialize-responsen kom. {initializeStderr}".Trim());
                }

                firstRequestIndex = 1;
            }

            foreach (var line in requests.Skip(firstRequestIndex))
                await process.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);

            phase = "statusrespons";
            var result = await ReadJsonRpcResultAsync(process, command, responseId, timeout.Token);
            if (result != null)
                return result;

            var stderr = await errorTask;
            throw new InvalidOperationException($"{command} avsluttet før statusresponsen kom. {stderr}".Trim());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Statuslesing fra {command} ble tidsavbrutt etter {timeoutSeconds} sekunder under {phase}. Kjørte {executablePath}.");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string?> ReadJsonRpcResultAsync(Process process, string command, long responseId, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt64() != responseId)
                continue;

            if (root.TryGetProperty("result", out var result))
                return result.GetRawText();

            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetRawText() : "ukjent feil";
            throw new InvalidOperationException($"{command} returnerte en feil ved statuslesing: {error}");
        }

        return null;
    }

    private async Task ProcessOutputLineAsync(
        Task previousLine,
        string line,
        Func<string, CancellationToken, Task> onOutputLine,
        TaskCompletionSource outputCompletion,
        CancellationToken cancellationToken)
    {
        await previousLine;

        try
        {
            await onOutputLine(line, cancellationToken);
        }
        catch (Exception exception)
        {
            outputCompletion.TrySetException(exception);
        }
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        return string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
    }

    private static Process StartProcess(string executablePath, List<string> arguments, string workingDirectory, bool redirectStdin)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (redirectStdin)
            // Codex app-server uses JSON Lines and expects the first byte to be '{', not a UTF-8 BOM.
            startInfo.StandardInputEncoding = Utf8WithoutBom;

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return new Process { StartInfo = startInfo };
    }

    private static void AppendIfNotNull(StringBuilder builder, string? data)
    {
        if (data != null)
            builder.AppendLine(data);
    }

    private async Task WriteStdinAsync(Process process, string? stdinInput, CancellationToken cancellationToken)
    {
        if (stdinInput == null)
            return;

        try
        {
            await process.StandardInput.WriteAsync(stdinInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            _logger.LogDebug("Standardinngang ble lukket av prosessen før skriving var ferdig");
        }
    }

    private async Task WaitForExitAsync(Process process, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            throw new TimeoutException("Kommandoutføring ble tidsavbrutt.");
        }
    }

    private string BuildResultOrThrow(int exitCode, string command, StringBuilder outputBuilder, StringBuilder errorBuilder)
    {
        ThrowIfError(exitCode, command, errorBuilder);
        return outputBuilder.ToString().Trim();
    }

    private void ThrowIfError(int exitCode, string command, StringBuilder errorBuilder)
    {
        var error = errorBuilder.ToString();

        if (exitCode == 0)
        {
            if (!string.IsNullOrWhiteSpace(error))
                _logger.LogDebug("{Command} stderr: {Error}", command, error);

            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
            _logger.LogError("{Command} stderr: {Error}", command, error);

        _logger.LogError("Kommando feilet med exit-kode {ExitCode}: {Error}", exitCode, error);

        var quotaException = QuotaExceededException.TryParse(error);
        if (quotaException != null)
            throw quotaException;

        throw new InvalidOperationException($"Kommando feilet: {error}");
    }

    private static async Task WaitForExitWithOutputAsync(Process process, TaskCompletionSource outputCompletion, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
            await outputCompletion.Task;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            throw new TimeoutException("Kommandoutføring ble tidsavbrutt.");
        }
    }
}
