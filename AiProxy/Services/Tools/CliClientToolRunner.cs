using AiProxy.Contracts;

namespace AiProxy.Services.Tools;

/// <summary>
/// Kjører én verktøytur for en CLI-provider: bygger verktøyinstruksjonen, lar provideren utføre
/// prompten slik den selv vil, og oversetter svaret til et kall klienten kan utføre. Runneren
/// starter ingen prosess selv, så den passer for alle CLI-ene uansett hvordan de kjøres.
/// </summary>
internal sealed class CliClientToolRunner
{
    private readonly string _providerName;
    private readonly string _callIdPrefix = "call_aiproxy_";
    private readonly ClientToolInstructionOptions _instructionOptions = new()
    {
        ExtraParagraphs =
        [
            "Your own built-in tools are disabled for this turn. Every file read, file change, command, and search must go through a declared client function; there is no other way to reach the user's workspace. Do not describe an action as done until its tool result confirms it.",
            "For an explicit request to run a command, build, or test, use a declared command-execution function directly when one is available. Do not replace that request with a speculative search."
        ]
    };

    internal CliClientToolRunner(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        _providerName = providerName;
    }

    internal async Task<OpenAiToolExecutionResult> RunAsync(
        OpenAiChatRequest request,
        string basePrompt,
        Func<string, CancellationToken, Task<string>> executePromptAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executePromptAsync);

        var prompt = ClientToolProtocol.AppendInstruction(
            basePrompt,
            request.FunctionTools,
            request.ToolChoice,
            request.PreviousToolCalls,
            request.ToolResults,
            _instructionOptions);

        var answer = await executePromptAsync(prompt, cancellationToken);
        var call = ClientToolProtocol.Parse(answer, request.FunctionTools, _callIdPrefix);

        if (call == null)
            return new OpenAiToolExecutionResult(answer, null);

        if (!ClientToolProtocol.IsAllowedByChoice(request.ToolChoice, call.Name))
            throw new InvalidOperationException($"{_providerName} forsøkte et verktøykall som tool_choice ikke tillater.");

        return new OpenAiToolExecutionResult(string.Empty, call);
    }
}
