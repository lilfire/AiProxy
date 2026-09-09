using System.Text.Json;
using AiProxy.Services;

namespace AiProxy.Services.Providers;

/// <summary>
/// Plukker ut TodoWrite-kall fra assistenthendelsene til Claude CLI og lagrer siste
/// øyeblikksbilde. Verdiene kopieres ut som strenger med én gang, fordi JsonElement-ene
/// er bundet til JsonDocument-et for linjen som akkurat ble lest.
/// </summary>
internal sealed class ClaudeTodoBlockReader
{
    private readonly ITodoSnapshotStore _todoStore;

    public ClaudeTodoBlockReader(ITodoSnapshotStore todoStore)
    {
        ArgumentNullException.ThrowIfNull(todoStore);

        _todoStore = todoStore;
    }

    public void ReadTodoBlocks(JsonStreamEvent streamEvent)
    {
        if (streamEvent.Type != ClaudeStreamConstants.AssistantEventType)
            return;

        if (streamEvent.Message.ValueKind != JsonValueKind.Object)
            return;

        if (!streamEvent.Message.TryGetProperty(ClaudeStreamConstants.ContentProperty, out var content))
            return;

        if (content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
            ReadTodoBlock(block);
    }

    private void ReadTodoBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return;

        if (!HasStringValue(block, ClaudeStreamConstants.TypeProperty, ClaudeStreamConstants.ToolUseBlockType))
            return;

        if (!HasStringValue(block, ClaudeStreamConstants.NameProperty, ClaudeStreamConstants.TodoWriteToolName))
            return;

        if (!block.TryGetProperty(ClaudeStreamConstants.InputProperty, out var input) || input.ValueKind != JsonValueKind.Object)
            return;

        if (!input.TryGetProperty(ClaudeStreamConstants.TodosProperty, out var todos) || todos.ValueKind != JsonValueKind.Array)
            return;

        _todoStore.Capture(new TodoSnapshot(ReadItems(todos)));
    }

    /// <summary>Et tomt array er gyldig: Claude tømmer lista slik. Siste kall vinner.</summary>
    private List<TodoSnapshotItem> ReadItems(JsonElement todos)
    {
        var items = new List<TodoSnapshotItem>();

        foreach (var todo in todos.EnumerateArray())
        {
            var item = ReadItem(todo);

            if (item != null)
                items.Add(item);
        }

        return items;
    }

    private TodoSnapshotItem? ReadItem(JsonElement todo)
    {
        if (todo.ValueKind != JsonValueKind.Object)
            return null;

        var content = GetString(todo, ClaudeStreamConstants.ContentProperty);

        if (string.IsNullOrWhiteSpace(content))
            return null;

        var status = GetString(todo, ClaudeStreamConstants.StatusProperty) ?? string.Empty;

        return new TodoSnapshotItem(content, status);
    }

    private bool HasStringValue(JsonElement element, string propertyName, string expectedValue)
    {
        var value = GetString(element, propertyName);

        return value == expectedValue;
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
