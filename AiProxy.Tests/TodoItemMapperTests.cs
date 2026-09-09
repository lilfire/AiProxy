using AiProxy.Application.Services;
using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class TodoItemMapperTests
{
    [TestMethod]
    public void Map_to_client_items_maps_known_status_one_to_one()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem>
        {
            new("Fiks bug", "pending"),
            new("Skriv test", "in_progress"),
            new("Rydd opp", "completed")
        };

        var result = mapper.MapToClientItems(items, FullSchema());

        Assert.AreEqual("pending", result[0].Status);
        Assert.AreEqual("in_progress", result[1].Status);
        Assert.AreEqual("completed", result[2].Status);
    }

    [TestMethod]
    public void Map_to_client_items_maps_unknown_status_to_pending()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem> { new("Fiks bug", "noe_helt_annet") };

        var result = mapper.MapToClientItems(items, FullSchema());

        Assert.AreEqual("pending", result[0].Status);
    }

    [TestMethod]
    public void Map_to_client_items_keeps_content_unchanged()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem> { new("Fiks bug i sesjonshåndtering", "pending") };

        var result = mapper.MapToClientItems(items, FullSchema());

        Assert.AreEqual("Fiks bug i sesjonshåndtering", result[0].Content);
    }

    [TestMethod]
    public void Map_to_client_items_sets_medium_priority_when_schema_supports_it()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem> { new("Fiks bug", "pending") };

        var result = mapper.MapToClientItems(items, FullSchema());

        Assert.AreEqual("medium", result[0].Priority);
    }

    [TestMethod]
    public void Map_to_client_items_omits_priority_when_schema_does_not_support_it()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem> { new("Fiks bug", "pending") };

        var result = mapper.MapToClientItems(items, new TodoToolSchema(true, "todowrite", SupportsId: true, SupportsPriority: false));

        Assert.IsNull(result[0].Priority);
    }

    [TestMethod]
    public void Map_to_client_items_generates_sequential_id_when_schema_supports_it()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem>
        {
            new("Fiks bug", "pending"),
            new("Skriv test", "pending")
        };

        var result = mapper.MapToClientItems(items, FullSchema());

        Assert.AreEqual("todo-1", result[0].Id);
        Assert.AreEqual("todo-2", result[1].Id);
    }

    [TestMethod]
    public void Map_to_client_items_omits_id_when_schema_does_not_support_it()
    {
        var mapper = new TodoItemMapper();
        var items = new List<TodoSnapshotItem> { new("Fiks bug", "pending") };

        var result = mapper.MapToClientItems(items, new TodoToolSchema(true, "todowrite", SupportsId: false, SupportsPriority: true));

        Assert.IsNull(result[0].Id);
    }

    [TestMethod]
    public void Map_to_client_items_returns_empty_list_for_empty_snapshot()
    {
        var mapper = new TodoItemMapper();

        var result = mapper.MapToClientItems(new List<TodoSnapshotItem>(), FullSchema());

        Assert.AreEqual(0, result.Count);
    }

    private TodoToolSchema FullSchema()
    {
        return new TodoToolSchema(true, "todowrite", SupportsId: true, SupportsPriority: true);
    }
}
