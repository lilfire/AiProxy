using AiProxy.Contracts;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            "Functions whose names start with `metamcp_` are active client-proxied MCP functions. They are available even though no MCP server is configured inside this CLI process. Never ask the user to install, enable, or reconnect an MCP server when a declared `metamcp_` function can handle the request; emit that function call instead.",
            "A returned client tool result proves that the client executed the function. Use its output to continue the task. Never claim that a function is not callable after receiving its result.",
            "A user may refer to an MCP server or capability by a friendly label such as `code-review`; that label does not need to literally match a declared function name. Match the requested capability to the declared functions by name and description. In particular, an Azure DevOps pull-request review should begin with a declared `metamcp_azure-devops__repo_pull_request` function, then use declared repository-file or pull-request functions as needed. Do not refuse merely because the friendly MCP label is not a function name.",
            "Pull-request metadata and a changed-file list are not sufficient for a code review. When `metamcp_azure-devops__repo_file` is declared, it is available in this turn: use it to retrieve the relevant changed files at the pull request's source and target revisions before reviewing them. Continue the tool loop until the required file contents or diff have been requested. Do not say that repository file contents, a diff, or a repository MCP is unavailable while this declared function exists.",
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

        var pendingReviewCall = GetInitialAzureDevOpsReviewCall(request) ?? GetNextAzureDevOpsReviewFileCall(request);
        if (pendingReviewCall != null)
            return new OpenAiToolExecutionResult(string.Empty, pendingReviewCall);

        var prompt = ClientToolProtocol.AppendInstruction(
            basePrompt,
            request.FunctionTools,
            request.ToolChoice,
            request.PreviousToolCalls,
            request.ToolResults,
            _instructionOptions);

        var answer = await executePromptAsync(prompt, cancellationToken);
        var call = ClientToolProtocol.Parse(answer, request.FunctionTools, _callIdPrefix);

        // En modell kan av og til beskrive neste nødvendige PR-filoppslag i prosa i stedet for
        // å sende det deklarerte klientkallet. Gi den én eksplisitt reparasjonstur slik at
        // OpenCode får et faktisk function_call å utføre.
        if (call == null && RequiresAzureDevOpsReviewFileCall(request, answer))
        {
            var repairPrompt = prompt + "\n\n<tool-call-repair>" +
                "Your previous response said that it needs PR file contents or a diff. That response is invalid because " +
                "`metamcp_azure-devops__repo_file` is declared and available. Return exactly one `tool_call` fence now for that declared function, " +
                "using its schema and the pull-request data already available in this session. Do not return prose, ask the user for data, or mention MCP availability." +
                "\n</tool-call-repair>";
            answer = await executePromptAsync(repairPrompt, cancellationToken);
            call = ClientToolProtocol.Parse(answer, request.FunctionTools, _callIdPrefix);
        }

        for (var attempt = 0; attempt < 2 && call == null && ClaimsCompletedClientToolsAreUnavailable(request, answer); attempt++)
        {
            var needsReviewFiles = request.Messages.Any(message =>
                    message.Content?.Contains("code-review", StringComparison.OrdinalIgnoreCase) == true ||
                    message.Content?.Contains("code review", StringComparison.OrdinalIgnoreCase) == true) &&
                request.FunctionTools.Any(tool => tool.Name == "metamcp_azure-devops__repo_file");
            var repairPrompt = prompt + "\n\n<tool-result-repair>" +
                "Your previous response incorrectly said that declared client tools are not callable. " +
                "The tool responses above prove that the client already executed those functions. " +
                (needsReviewFiles
                    ? "Continue the pull-request review by returning exactly one `tool_call` fence for the declared " +
                      "`metamcp_azure-devops__repo_file` function. Read the next changed file or its target revision " +
                      "using the pull-request metadata above. Use the same action, project, repositoryId, path, version, " +
                      "and versionType argument names as the completed repository-file call. Do not give a final answer yet. "
                    : "Continue the user's task using those results. If more data is needed, return exactly one `tool_call` fence " +
                      "for a declared function; otherwise provide the answer. ") +
                "Do not repeat the availability claim. " +
                (attempt == 1 ? "This is the final retry. Return the call as fenced JSON now." : string.Empty) +
                "\n</tool-result-repair>";
            answer = await executePromptAsync(repairPrompt, cancellationToken);
            call = ClientToolProtocol.Parse(answer, request.FunctionTools, _callIdPrefix);
        }

        if (call == null)
            return new OpenAiToolExecutionResult(answer, null);

        if (!ClientToolProtocol.IsAllowedByChoice(request.ToolChoice, call.Name))
            throw new InvalidOperationException($"{_providerName} forsøkte et verktøykall som tool_choice ikke tillater.");

        return new OpenAiToolExecutionResult(string.Empty, call);
    }

    private static bool RequiresAzureDevOpsReviewFileCall(OpenAiChatRequest request, string answer)
    {
        if (ClientToolProtocol.RequiresAzureDevOpsFileCall(answer, request.FunctionTools))
            return true;

        var hasRepositoryFileTool = request.FunctionTools.Any(tool =>
            string.Equals(tool.Name, "metamcp_azure-devops__repo_file", StringComparison.Ordinal));
        var hasCompletedPullRequestCall = request.PreviousToolCalls.Any(call =>
                string.Equals(call.Name, "metamcp_azure-devops__repo_pull_request", StringComparison.Ordinal)) &&
            request.ToolResults.Any(result => request.PreviousToolCalls.Any(call =>
                call.Id == result.CallId &&
                string.Equals(call.Name, "metamcp_azure-devops__repo_pull_request", StringComparison.Ordinal)));
        var hasCompletedRepositoryFileCall = request.PreviousToolCalls.Any(call =>
                string.Equals(call.Name, "metamcp_azure-devops__repo_file", StringComparison.Ordinal)) &&
            request.ToolResults.Any(result => request.PreviousToolCalls.Any(call =>
                call.Id == result.CallId &&
                string.Equals(call.Name, "metamcp_azure-devops__repo_file", StringComparison.Ordinal)));
        var isCodeReview = request.Messages.Any(message =>
            message.Content?.Contains("code-review", StringComparison.OrdinalIgnoreCase) == true ||
            message.Content?.Contains("code review", StringComparison.OrdinalIgnoreCase) == true);

        return hasRepositoryFileTool && isCodeReview && hasCompletedPullRequestCall && !hasCompletedRepositoryFileCall;
    }

    private static bool ClaimsCompletedClientToolsAreUnavailable(OpenAiChatRequest request, string answer)
    {
        if (!request.ToolResults.Any(result => request.PreviousToolCalls.Any(call =>
                call.Id == result.CallId && request.FunctionTools.Any(tool => tool.Name == call.Name))))
            return false;

        return answer.Contains("client tools are not callable", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("declared tools are not callable", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("MCP tools are not callable", StringComparison.OrdinalIgnoreCase) ||
            answer.Contains("no callable client tool interface", StringComparison.OrdinalIgnoreCase) ||
            (answer.Contains("client-proxied tools", StringComparison.OrdinalIgnoreCase) &&
                answer.Contains("not available", StringComparison.OrdinalIgnoreCase)) ||
            (answer.Contains("MCP calls", StringComparison.OrdinalIgnoreCase) &&
                (answer.Contains("aren't available", StringComparison.OrdinalIgnoreCase) ||
                    answer.Contains("aren’t available", StringComparison.OrdinalIgnoreCase))) ||
            (answer.Contains("unable to continue", StringComparison.OrdinalIgnoreCase) &&
                answer.Contains("tool loop", StringComparison.OrdinalIgnoreCase));
    }

    private OpenAiProviderToolCall? GetNextAzureDevOpsReviewFileCall(OpenAiChatRequest request)
    {
        if (!request.Messages.Any(message =>
                message.Role == OpenAiConstants.Roles.User &&
                (message.Content?.Contains("code-review", StringComparison.OrdinalIgnoreCase) == true ||
                 message.Content?.Contains("code review", StringComparison.OrdinalIgnoreCase) == true)))
            return null;

        var fileTool = request.FunctionTools.FirstOrDefault(tool =>
            tool.Name == "metamcp_azure-devops__repo_file");
        if (fileTool == null || !ClientToolProtocol.IsAllowedByChoice(request.ToolChoice, fileTool.Name))
            return null;

        foreach (var prCall in request.PreviousToolCalls.Where(call =>
                     call.Name == "metamcp_azure-devops__repo_pull_request"))
        {
            var prResult = request.ToolResults.FirstOrDefault(result => result.CallId == prCall.Id);
            if (prResult == null)
                continue;

            var start = prResult.Output.IndexOf('{');
            var end = prResult.Output.LastIndexOf('}');
            if (start < 0 || end <= start)
                continue;

            try
            {
                using var document = JsonDocument.Parse(prResult.Output[(start)..(end + 1)]);
                var root = document.RootElement;
                if (!root.TryGetProperty("repository", out var repository) ||
                    !repository.TryGetProperty("name", out var repositoryName) ||
                    !repository.TryGetProperty("project", out var project) ||
                    !project.TryGetProperty("name", out var projectName) ||
                    !root.TryGetProperty("lastMergeSourceCommit", out var sourceCommit) ||
                    !sourceCommit.TryGetProperty("commitId", out var sourceVersion) ||
                    !root.TryGetProperty("lastMergeTargetCommit", out var targetCommit) ||
                    !targetCommit.TryGetProperty("commitId", out var targetVersion) ||
                    !root.TryGetProperty("changedFilesSummary", out var summary) ||
                    !summary.TryGetProperty("changeEntries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                var projectValue = projectName.GetString();
                var repositoryValue = repositoryName.GetString();
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("item", out var item) ||
                        !item.TryGetProperty("path", out var pathElement))
                        continue;

                    var path = pathElement.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    foreach (var versionElement in new[] { sourceVersion, targetVersion })
                    {
                        var version = versionElement.GetString();
                        if (string.IsNullOrWhiteSpace(version) || request.PreviousToolCalls.Any(call =>
                                call.Name == fileTool.Name && IsSameRepositoryFileCall(call.ArgumentsJson, path, version)))
                            continue;

                        var arguments = JsonSerializer.Serialize(new
                        {
                            action = "get_content",
                            project = projectValue,
                            repositoryId = repositoryValue,
                            path,
                            version,
                            versionType = "Commit"
                        });
                        return new OpenAiProviderToolCall(
                            _callIdPrefix + Guid.NewGuid().ToString("N"), fileTool.Name, arguments, fileTool.Type);
                    }
                }
            }
            catch (JsonException)
            {
                // Other MCP implementations may return non-JSON PR data; let the model handle it.
            }
        }

        return null;
    }

    private OpenAiProviderToolCall? GetInitialAzureDevOpsReviewCall(OpenAiChatRequest request)
    {
        var prTool = request.FunctionTools.FirstOrDefault(tool =>
            tool.Name == "metamcp_azure-devops__repo_pull_request");
        if (prTool == null || !ClientToolProtocol.IsAllowedByChoice(request.ToolChoice, prTool.Name) ||
            request.PreviousToolCalls.Any(call => call.Name == prTool.Name))
            return null;

        var userMessage = request.Messages.LastOrDefault(message => message.Role == OpenAiConstants.Roles.User)?.Content;
        if (string.IsNullOrWhiteSpace(userMessage) ||
            (!userMessage.Contains("code-review", StringComparison.OrdinalIgnoreCase) &&
             !userMessage.Contains("code review", StringComparison.OrdinalIgnoreCase)))
            return null;

        var id = Regex.Match(userMessage, @"\b(?:pr|pull request)\s*#?(?<value>\d+)\b", RegexOptions.IgnoreCase);
        var project = Regex.Match(userMessage, @"\bproject\s+(?<value>[\w.-]+)", RegexOptions.IgnoreCase);
        var repository = Regex.Match(userMessage, @"\brepo(?:sitory)?\s+(?<value>[\w.-]+)", RegexOptions.IgnoreCase);
        if (!id.Success || !project.Success || !repository.Success ||
            !int.TryParse(id.Groups["value"].Value, out var pullRequestId))
            return null;

        var arguments = JsonSerializer.Serialize(new
        {
            action = "get",
            project = project.Groups["value"].Value,
            repositoryId = repository.Groups["value"].Value,
            pullRequestId,
            includeChangedFiles = true,
            includeWorkItemRefs = true,
            includeLabels = true
        });
        return new OpenAiProviderToolCall(
            _callIdPrefix + Guid.NewGuid().ToString("N"), prTool.Name, arguments, prTool.Type);
    }

    private static bool IsSameRepositoryFileCall(string argumentsJson, string path, string version)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            return root.TryGetProperty("path", out var previousPath) &&
                root.TryGetProperty("version", out var previousVersion) &&
                string.Equals(previousPath.GetString(), path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previousVersion.GetString(), version, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
