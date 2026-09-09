using AiProxy.Application.Services;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Interfaces;

public interface ITodoItemMapper
{
    List<OpenAiTodoItem> MapToClientItems(IReadOnlyList<TodoSnapshotItem> items, TodoToolSchema schema);
}
