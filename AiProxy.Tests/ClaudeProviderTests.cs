using System.Text.Json.Nodes;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ClaudeProviderTests
{
    private readonly string _promptFilePath = """C:\prompt.txt""";

    [TestMethod]
    public void BuildArguments_new_session_includes_session_id_and_stream_json_flags()
    {
        var provider = CreateProvider();
        var request = new OpenAiChatRequest { Model = "sonnet" };

        var arguments = provider.BuildArguments(request, _promptFilePath, "sess-123", true);

        CollectionAssert.Contains(arguments, "--session-id");
        CollectionAssert.Contains(arguments, "sess-123");
        CollectionAssert.DoesNotContain(arguments, "--resume");
        CollectionAssert.Contains(arguments, "-p");
        CollectionAssert.Contains(arguments, $"@{_promptFilePath}");
        CollectionAssert.Contains(arguments, "--model");
        CollectionAssert.Contains(arguments, "sonnet");
        CollectionAssert.Contains(arguments, "--output-format");
        CollectionAssert.Contains(arguments, "stream-json");
        CollectionAssert.Contains(arguments, "--include-partial-messages");
        CollectionAssert.Contains(arguments, "--verbose");
        CollectionAssert.Contains(arguments, "--dangerously-skip-permissions");
    }

    [TestMethod]
    public void BuildArguments_existing_session_uses_resume()
    {
        var provider = CreateProvider();
        var request = new OpenAiChatRequest { Model = "opus" };

        var arguments = provider.BuildArguments(request, _promptFilePath, "sess-456", false);

        CollectionAssert.DoesNotContain(arguments, "--session-id");
        CollectionAssert.Contains(arguments, "--resume");
        CollectionAssert.Contains(arguments, "sess-456");
    }

    [TestMethod]
    public async Task Extract_assistant_text_empty_input_returns_empty_string()
    {
        var provider = CreateProvider();

        var result = await provider.ExtractAssistantTextAsync(string.Empty);

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public async Task Extract_assistant_text_plain_text_input_returns_input_unchanged()
    {
        var provider = CreateProvider();
        var input = "Dette er et svar fra Claude.";

        var result = await provider.ExtractAssistantTextAsync(input);

        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public async Task Extract_assistant_text_returns_answer_once_when_all_three_sources_repeat_it()
    {
        var provider = CreateProvider();
        var input = string.Join(Environment.NewLine, new[]
        {
            SystemInitLine(),
            TextDeltaLine("Hei "),
            TextDeltaLine("på deg"),
            AssistantTextLine("Hei på deg"),
            ResultLine("Hei på deg")
        });

        var result = await provider.ExtractAssistantTextAsync(input);

        Assert.AreEqual("Hei på deg", result);
    }

    [TestMethod]
    public async Task Extract_assistant_text_mixed_json_and_plain_text_falls_back_to_raw_output()
    {
        var provider = CreateProvider();
        var input = string.Join(Environment.NewLine, new[] { SystemInitLine(), "Dette er ikke JSON" });

        var result = await provider.ExtractAssistantTextAsync(input);

        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public async Task Extract_assistant_text_valid_json_but_no_text_content_returns_empty_string()
    {
        var provider = CreateProvider();

        var result = await provider.ExtractAssistantTextAsync(SystemInitLine());

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public async Task Execute_streaming_emits_each_text_delta_once()
    {
        var chunks = await StreamLinesAsync([
            TextDeltaLine("Hei "),
            TextDeltaLine("på deg"),
            ResultLine("Hei på deg")
        ]);

        CollectionAssert.AreEqual(new[] { "Hei ", "på deg" }, chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_skips_assistant_and_result_that_repeat_streamed_text()
    {
        var chunks = await StreamLinesAsync([
            TextDeltaLine("eple banan"),
            AssistantTextLine("eple banan"),
            ResultLine("eple banan")
        ]);

        CollectionAssert.AreEqual(new[] { "eple banan" }, chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_falls_back_to_assistant_when_partial_messages_are_missing()
    {
        var chunks = await StreamLinesAsync([
            AssistantTextLine("eple banan"),
            ResultLine("eple banan")
        ]);

        CollectionAssert.AreEqual(new[] { "eple banan" }, chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_falls_back_to_result_when_nothing_else_carried_text()
    {
        var chunks = await StreamLinesAsync([SystemInitLine(), ResultLine("eple banan")]);

        CollectionAssert.AreEqual(new[] { "eple banan" }, chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_ignores_thinking_block_and_its_assistant_event()
    {
        var chunks = await StreamLinesAsync([
            ThinkingDeltaLine("vurderer"),
            AssistantThinkingLine("vurderer"),
            TextDeltaLine("eple banan"),
            AssistantTextLine("eple banan"),
            ResultLine("eple banan")
        ]);

        CollectionAssert.AreEqual(new[] { "eple banan" }, chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_emits_each_text_block_once_when_message_has_several()
    {
        var chunks = await StreamLinesAsync([
            TextDeltaLine("første"),
            AssistantTextLine("første"),
            TextDeltaLine("andre"),
            AssistantTextLine("andre"),
            ResultLine("andre")
        ]);

        CollectionAssert.AreEqual(new[] { "første", "andre" }, chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_emits_assistant_text_blocks_and_ignores_thinking()
    {
        var chunks = await StreamLinesAsync([
            AssistantTextLine("Hello"),
            AssistantThinkingLine("should ignore"),
            AssistantTextLine(" world")
        ]);

        CollectionAssert.AreEqual(new[] { "Hello", " world" }, chunks);
    }

    [TestMethod]
    public async Task Execute_collects_streaming_chunks_into_full_text()
    {
        var request = new OpenAiChatRequest { Model = "haiku" };
        var shellRunner = new FakeStreamingShellRunner();
        var provider = CreateProvider(shellRunner);

        shellRunner.QueueOutputLines([
            TextDeltaLine("Dette "),
            TextDeltaLine("ble riktig."),
            AssistantTextLine("Dette ble riktig."),
            ResultLine("Dette ble riktig.")
        ]);

        var result = await provider.ExecuteAsync(request, "session-1");

        Assert.AreEqual("Dette ble riktig.", result);
    }

    [TestMethod]
    public async Task Execute_streaming_captures_todo_snapshot_from_tool_use_block()
    {
        var result = await StreamWithTodosAsync([
            SystemInitLine(),
            TodoWriteLine(Todo("Fiks bug", "in_progress"), Todo("Skriv test", "pending"))
        ]);

        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(2, result.Snapshot.Items.Count);
        Assert.AreEqual("Fiks bug", result.Snapshot.Items[0].Content);
        Assert.AreEqual("in_progress", result.Snapshot.Items[0].Status);
        Assert.AreEqual("Skriv test", result.Snapshot.Items[1].Content);
    }

    [TestMethod]
    public async Task Execute_streaming_captures_todos_even_when_text_was_streamed_first()
    {
        var result = await StreamWithTodosAsync([
            TextDeltaLine("Jeg starter nå."),
            TodoWriteLine(Todo("Fiks bug", "pending"))
        ]);

        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(1, result.Snapshot.Items.Count);
        CollectionAssert.AreEqual(new List<string> { "Jeg starter nå." }, result.Chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_captures_todos_when_text_and_tool_use_share_one_assistant_message()
    {
        var result = await StreamWithTodosAsync([
            AssistantBlocksLine(
                new JsonObject { ["type"] = "text", ["text"] = "Planen min:" },
                new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_1",
                    ["name"] = "TodoWrite",
                    ["input"] = new JsonObject { ["todos"] = new JsonArray(Todo("Fiks bug", "pending")) }
                })
        ]);

        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(1, result.Snapshot.Items.Count);
        CollectionAssert.AreEqual(new List<string> { "Planen min:" }, result.Chunks);
    }

    [TestMethod]
    public async Task Execute_streaming_captures_last_snapshot_when_todo_tool_is_called_twice()
    {
        var result = await StreamWithTodosAsync([
            TodoWriteLine(Todo("Fiks bug", "in_progress")),
            TodoWriteLine(Todo("Fiks bug", "completed"), Todo("Skriv test", "in_progress"))
        ]);

        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(2, result.Snapshot.Items.Count);
        Assert.AreEqual("completed", result.Snapshot.Items[0].Status);
    }

    [TestMethod]
    public async Task Execute_streaming_does_not_emit_tool_use_block_as_text()
    {
        var result = await StreamWithTodosAsync([
            TodoWriteLine(Todo("Fiks bug", "pending"))
        ]);

        Assert.AreEqual(0, result.Chunks.Count);
    }

    [TestMethod]
    public async Task Execute_streaming_ignores_tool_use_blocks_from_other_tools()
    {
        var result = await StreamWithTodosAsync([
            AssistantBlocksLine(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = "toolu_9",
                ["name"] = "Read",
                ["input"] = new JsonObject { ["file_path"] = "C:\temp\fil.txt" }
            })
        ]);

        Assert.IsNull(result.Snapshot);
    }

    [TestMethod]
    public async Task Execute_streaming_captures_empty_todo_list_when_claude_clears_it()
    {
        var result = await StreamWithTodosAsync([
            TodoWriteLine(Todo("Fiks bug", "in_progress")),
            TodoWriteLine()
        ]);

        Assert.IsNotNull(result.Snapshot);
        Assert.AreEqual(0, result.Snapshot.Items.Count);
    }

    [TestMethod]
    public async Task Execute_streaming_leaves_snapshot_empty_when_no_todo_tool_is_used()
    {
        var result = await StreamWithTodosAsync([
            TextDeltaLine("Bare tekst.")
        ]);

        Assert.IsNull(result.Snapshot);
    }

    private async Task<List<string>> StreamLinesAsync(IEnumerable<string> lines)
    {
        var result = await StreamWithTodosAsync(lines);

        return result.Chunks;
    }

    private async Task<ClaudeStreamResult> StreamWithTodosAsync(IEnumerable<string> lines)
    {
        var shellRunner = new FakeStreamingShellRunner();
        shellRunner.QueueOutputLines(lines);

        var todoStore = new TodoSnapshotStore();
        var provider = CreateProvider(shellRunner, todoStore);
        var request = new OpenAiChatRequest { Model = "sonnet" };
        var chunks = new List<string>();

        await provider.ExecuteStreamingAsync(request, "session-1", (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        return new ClaudeStreamResult(chunks, todoStore.Snapshot);
    }

    private string TodoWriteLine(params JsonObject[] todos)
    {
        return AssistantBlocksLine(new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = "toolu_1",
            ["name"] = "TodoWrite",
            ["input"] = new JsonObject
            {
                ["todos"] = new JsonArray(todos.Cast<JsonNode>().ToArray())
            }
        });
    }

    private JsonObject Todo(string content, string status)
    {
        return new JsonObject
        {
            ["content"] = content,
            ["status"] = status,
            ["activeForm"] = content
        };
    }

    private string AssistantBlocksLine(params JsonObject[] contentBlocks)
    {
        return new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(contentBlocks.Cast<JsonNode>().ToArray())
            }
        }.ToJsonString();
    }

    private string SystemInitLine()
    {
        return new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init"
        }.ToJsonString();
    }

    private string TextDeltaLine(string text)
    {
        return ContentBlockDeltaLine(new JsonObject
        {
            ["type"] = "text_delta",
            ["text"] = text
        });
    }

    private string ThinkingDeltaLine(string thinking)
    {
        return ContentBlockDeltaLine(new JsonObject
        {
            ["type"] = "thinking_delta",
            ["thinking"] = thinking
        });
    }

    private string ContentBlockDeltaLine(JsonObject delta)
    {
        return new JsonObject
        {
            ["type"] = "stream_event",
            ["event"] = new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = 0,
                ["delta"] = delta
            }
        }.ToJsonString();
    }

    private string AssistantTextLine(string text)
    {
        return AssistantLine(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text
        });
    }

    private string AssistantThinkingLine(string thinking)
    {
        return AssistantLine(new JsonObject
        {
            ["type"] = "thinking",
            ["thinking"] = thinking
        });
    }

    private string AssistantLine(JsonObject contentBlock)
    {
        return new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray { contentBlock }
            }
        }.ToJsonString();
    }

    private string ResultLine(string text)
    {
        return new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["result"] = text
        }.ToJsonString();
    }

    private ClaudeProvider CreateProvider(ShellCommandRunner shellRunner)
    {
        return CreateProvider(shellRunner, new TodoSnapshotStore());
    }

    private ClaudeProvider CreateProvider(ShellCommandRunner shellRunner, ITodoSnapshotStore todoStore)
    {
        return new ClaudeProvider(NullLogger<ClaudeProvider>.Instance, shellRunner, new ProviderSessionStore(), new PromptFileWriter(), todoStore, new ImageInputResolver());
    }

    private ClaudeProvider CreateProvider()
    {
        var executablePathResolver = new ExecutablePathResolver();
        var shellRunner = new ShellCommandRunner(executablePathResolver, NullLogger<ShellCommandRunner>.Instance, new TestRuntimeSettings());

        return CreateProvider(shellRunner);
    }
}
