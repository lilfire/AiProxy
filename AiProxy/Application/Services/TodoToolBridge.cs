using System.Text.Json;
using AiProxy.Application.Interfaces;
using AiProxy.Configuration;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed class TodoToolBridge : ITodoToolBridge
{
    private readonly ITodoSnapshotStore _todoSnapshotStore;
    private readonly ITodoItemMapper _itemMapper;
    private readonly IRuntimeSettings _settings;
    private readonly ILogger<TodoToolBridge> _logger;

    private readonly TodoToolSchema _notDeclared = new(false, string.Empty, false, false);
    private readonly int _callIdLength = 12;

    public TodoToolBridge(
        ITodoSnapshotStore todoSnapshotStore,
        ITodoItemMapper itemMapper,
        IRuntimeSettings settings,
        ILogger<TodoToolBridge> logger)
    {
        _todoSnapshotStore = todoSnapshotStore;
        _itemMapper = itemMapper;
        _settings = settings;
        _logger = logger;
    }

    /// <remarks>
    /// Sjekker bevisst ikke TodoBridgeEnabled: slås broen av midt i en samtale må et allerede
    /// utstedt verktøykall fortsatt kunne fullføres.
    /// </remarks>
    public bool IsProxyToolResultFollowUp(OpenAiResponsesRequest request)
    {
        if (request.Input is not JsonElement input || input.ValueKind != JsonValueKind.Array)
            return false;

        var callIds = CollectTrailingToolOutputCallIds(input);

        if (callIds.Count == 0)
            return false;

        foreach (var callId in callIds)
        {
            if (!IsProxyCallId(callId))
                return false;
        }

        return true;
    }

    public TodoToolSchema ReadClientSchema(OpenAiResponsesRequest request)
    {
        if (!_settings.Current.TodoBridgeEnabled)
            return _notDeclared;

        if (request.Tools == null || request.Tools.Count == 0)
            return _notDeclared;

        var tool = FindTodoTool(request.Tools);

        if (tool == null)
            return _notDeclared;

        if (!IsToolChoiceAllowing(request.ToolChoice, tool.Name))
            return _notDeclared;

        var schema = ReadItemSchema(tool);

        _logger.LogInformation(
            "Klienten deklarerte todo-verktøyet {ToolName} (id: {SupportsId}, prioritet: {SupportsPriority})",
            schema.DeclaredName,
            schema.SupportsId,
            schema.SupportsPriority);

        return schema;
    }

    public OpenAiTodoCall? TryCreateTodoCall(TodoToolSchema schema)
    {
        if (!schema.IsDeclared)
            return null;

        var snapshot = _todoSnapshotStore.Snapshot;

        if (snapshot == null)
            return null;

        var items = _itemMapper.MapToClientItems(snapshot.Items, schema);
        var arguments = JsonSerializer.Serialize(new OpenAiTodoWriteArguments { Todos = items });
        var suffix = Guid.NewGuid().ToString("N")[.._callIdLength];

        _logger.LogInformation("Sender {TodoCount} todo-oppgaver videre som verktøykall {ToolName}", items.Count, schema.DeclaredName);

        return new OpenAiTodoCall(
            OpenAiConstants.ToolCalls.ProxyTodoItemIdPrefix + suffix,
            OpenAiConstants.ToolCalls.ProxyTodoCallIdPrefix + suffix,
            schema.DeclaredName,
            arguments);
    }

    /// <summary>Samler call_id fra verktøyresultatene som avslutter input-lista, bakfra.</summary>
    private List<string?> CollectTrailingToolOutputCallIds(JsonElement input)
    {
        var callIds = new List<string?>();

        for (var index = input.GetArrayLength() - 1; index >= 0; index--)
        {
            var item = input[index];

            if (!IsFunctionCallOutput(item))
                break;

            callIds.Add(GetString(item, OpenAiConstants.ToolSchemaProperties.CallId));
        }

        return callIds;
    }

    private bool IsFunctionCallOutput(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return false;

        return GetString(item, OpenAiConstants.ToolSchemaProperties.Type) == OpenAiConstants.ResponseInputTypes.FunctionCallOutput;
    }

    private bool IsProxyCallId(string? callId)
    {
        return callId != null && callId.StartsWith(OpenAiConstants.ToolCalls.ProxyTodoCallIdPrefix, StringComparison.Ordinal);
    }

    private OpenAiResponsesTool? FindTodoTool(List<OpenAiResponsesTool> tools)
    {
        foreach (var tool in tools)
        {
            if (IsTodoToolName(tool.Name))
                return tool;
        }

        return null;
    }

    private bool IsTodoToolName(string name)
    {
        return string.Equals(name, OpenAiConstants.ToolCalls.TodoWriteName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, OpenAiConstants.ToolCalls.TodoWriteSnakeName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsToolChoiceAllowing(JsonElement? toolChoice, string declaredName)
    {
        if (toolChoice == null)
            return true;

        var choice = toolChoice.Value;

        if (choice.ValueKind == JsonValueKind.String)
            return choice.GetString() != OpenAiConstants.ToolCalls.ToolChoiceNone;

        if (choice.ValueKind != JsonValueKind.Object)
            return true;

        var forcedName = GetString(choice, OpenAiConstants.ToolSchemaProperties.Name);

        return forcedName == null || forcedName == declaredName;
    }

    private TodoToolSchema ReadItemSchema(OpenAiResponsesTool tool)
    {
        var properties = FindTodoItemProperties(tool);

        // Uten lesbart skjema bruker vi den dokumenterte opencode-formen: content, status, priority.
        if (properties == null)
            return new TodoToolSchema(true, tool.Name, SupportsId: false, SupportsPriority: true);

        var supportsId = properties.Value.TryGetProperty(OpenAiConstants.ToolSchemaProperties.Id, out _);
        var supportsPriority = properties.Value.TryGetProperty(OpenAiConstants.ToolSchemaProperties.Priority, out _);

        return new TodoToolSchema(true, tool.Name, supportsId, supportsPriority);
    }

    private JsonElement? FindTodoItemProperties(OpenAiResponsesTool tool)
    {
        var properties = GetObjectProperty(tool.Parameters, OpenAiConstants.ToolSchemaProperties.Properties);
        var todos = GetObjectProperty(properties, OpenAiConstants.ToolSchemaProperties.Todos);
        var items = GetObjectProperty(todos, OpenAiConstants.ToolSchemaProperties.Items);

        return GetObjectProperty(items, OpenAiConstants.ToolSchemaProperties.Properties);
    }

    private JsonElement? GetObjectProperty(JsonElement? element, string propertyName)
    {
        if (element == null || element.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.Value.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind != JsonValueKind.Object)
            return null;

        return property;
    }

    private string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }
}
