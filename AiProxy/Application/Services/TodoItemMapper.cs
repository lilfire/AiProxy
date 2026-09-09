using AiProxy.Application.Interfaces;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

public sealed class TodoItemMapper : ITodoItemMapper
{
    private readonly string _idPrefix = "todo-";

    public List<OpenAiTodoItem> MapToClientItems(IReadOnlyList<TodoSnapshotItem> items, TodoToolSchema schema)
    {
        var result = new List<OpenAiTodoItem>();

        for (var index = 0; index < items.Count; index++)
            result.Add(MapItem(items[index], index, schema));

        return result;
    }

    private OpenAiTodoItem MapItem(TodoSnapshotItem item, int index, TodoToolSchema schema)
    {
        return new OpenAiTodoItem
        {
            Id = schema.SupportsId ? _idPrefix + (index + 1) : null,
            Content = item.Content,
            Status = MapStatus(item.Status),
            Priority = schema.SupportsPriority ? OpenAiConstants.TodoPriorities.Medium : null
        };
    }

    /// <summary>Ukjente provider-statuser faller tilbake til pending.</summary>
    private string MapStatus(string status)
    {
        if (status == OpenAiConstants.TodoStatuses.InProgress)
            return OpenAiConstants.TodoStatuses.InProgress;

        if (status == OpenAiConstants.TodoStatuses.Completed)
            return OpenAiConstants.TodoStatuses.Completed;

        if (status == OpenAiConstants.TodoStatuses.Cancelled)
            return OpenAiConstants.TodoStatuses.Cancelled;

        return OpenAiConstants.TodoStatuses.Pending;
    }
}
