using AiProxy.Application.Interfaces;
using AiProxy.Application.Services;
using AiProxy.Contracts;
using AiProxy.Services;
using AiProxy.Services.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

[TestClass]
public class ChatCompletionServiceTests
{
    [TestMethod]
    public async Task ExecuteAsync_non_streaming_returns_json_result()
    {
        var fakeProvider = new FakeChatProvider("Fake", new[] { "model-1" }, "Hello, world!");
        var registry = CreateRegistry(fakeProvider);
        var service = new ChatCompletionService(
            registry,
            new ChatRequestFactory(),
            new OpenAiStreamFormatter(),
            new ProviderUsageStore(),
            new InMemorySessionHistoryStore(),
            NullLogger<ChatCompletionService>.Instance);

        var request = new OpenAiChatRequest
        {
            Model = "model-1",
            Messages = new List<OpenAiMessage> { new("user", "Hi") },
            Stream = false
        };

        var result = await service.ExecuteAsync(request, "session-1");

        Assert.IsNotNull(result);
        Assert.AreEqual("model-1", fakeProvider.LastRequest?.Model);
        Assert.AreEqual("session-1", fakeProvider.LastSessionId);
    }

    [TestMethod]
    public async Task ExecuteAsync_non_streaming_maps_provider_model_id()
    {
        var fakeProvider = new FakeChatProvider("Fake", new[] { "internal-model" }, "Hello, world!");
        var registry = CreateRegistry(fakeProvider);
        var service = new ChatCompletionService(
            registry,
            new ChatRequestFactory(),
            new OpenAiStreamFormatter(),
            new ProviderUsageStore(),
            new InMemorySessionHistoryStore(),
            NullLogger<ChatCompletionService>.Instance);

        var request = new OpenAiChatRequest
        {
            Model = "internal-model",
            Messages = new List<OpenAiMessage> { new("user", "Hi") },
            Stream = false
        };

        await service.ExecuteAsync(request, "session-1");

        Assert.AreEqual("internal-model", fakeProvider.LastRequest?.Model);
    }

    [TestMethod]
    public async Task ExecuteAsync_non_streaming_returns_openai_tool_call_for_tool_aware_provider()
    {
        var provider = new FakeToolAwareChatProvider(["model-1"], new OpenAiToolExecutionResult("", new OpenAiProviderToolCall("call_client_7", "read_file", "{\"path\":\"a.txt\"}")));
        var service = new ChatCompletionService(
            CreateRegistry(provider),
            new ChatRequestFactory(),
            new OpenAiStreamFormatter(),
            new ProviderUsageStore(),
            new InMemorySessionHistoryStore(),
            NullLogger<ChatCompletionService>.Instance);
        var request = new OpenAiChatRequest
        {
            Model = "model-1",
            Stream = false,
            Messages = [new OpenAiMessage("user", "Read it")],
            Tools = [new OpenAiChatTool
            {
                Type = "function",
                Function = new OpenAiChatFunction { Name = "read_file", Description = "Read a file", Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }) }
            }]
        };

        var body = await new SseResultReader().ReadAsync(await service.ExecuteAsync(request, "session-1"));

        StringAssert.Contains(body, "\"tool_calls\"");
        StringAssert.Contains(body, "\"call_client_7\"");
        StringAssert.Contains(body, "\"finish_reason\":\"tool_calls\"");
        Assert.AreEqual("read_file", provider.LastToolRequest!.FunctionTools.Single().Name);
    }

    [TestMethod]
    public async Task ExecuteAsync_keeps_only_the_tool_exchange_after_the_last_user_message()
    {
        var provider = new FakeToolAwareChatProvider(["model-1"], new OpenAiToolExecutionResult("ferdig", null));
        var service = CreateToolService(provider);
        var request = new OpenAiChatRequest
        {
            Model = "model-1",
            Stream = false,
            Messages =
            [
                new OpenAiMessage("user", "Les a.txt"),
                AssistantCall("call_a", "read_file"),
                ToolResult("call_a", "innhold a"),
                new OpenAiMessage("user", "Les b.txt"),
                AssistantCall("call_b", "read_file"),
                ToolResult("call_b", "innhold b")
            ],
            Tools = [ReadFileTool()]
        };

        await service.ExecuteAsync(request, "session-1");

        var toolRequest = provider.LastToolRequest!;
        Assert.AreEqual("call_b", toolRequest.PreviousToolCalls.Single().Id);
        Assert.AreEqual("innhold b", toolRequest.ToolResults.Single().Output);
        Assert.IsTrue(toolRequest.HasToolResultsSinceLastUserMessage);
    }

    [TestMethod]
    public async Task ExecuteAsync_forwards_a_custom_tool_declaration_to_a_tool_aware_provider()
    {
        var provider = new FakeToolAwareChatProvider(["model-1"], new OpenAiToolExecutionResult("ferdig", null));
        var service = CreateToolService(provider);
        var request = new OpenAiChatRequest
        {
            Model = "model-1",
            Stream = false,
            Messages = [new OpenAiMessage("user", "Kjør den")],
            Tools = [new OpenAiChatTool
            {
                Type = "custom",
                Custom = new OpenAiChatFunction { Name = "shell", Description = "Kjører en kommando" }
            }]
        };

        await service.ExecuteAsync(request, "session-1");

        var declared = provider.LastToolRequest!.FunctionTools.Single();
        Assert.AreEqual("shell", declared.Name);
        Assert.AreEqual("custom", declared.Type);
    }

    private ChatCompletionService CreateToolService(IChatProvider provider) =>
        new(
            CreateRegistry(provider),
            new ChatRequestFactory(),
            new OpenAiStreamFormatter(),
            new ProviderUsageStore(),
            new InMemorySessionHistoryStore(),
            NullLogger<ChatCompletionService>.Instance);

    private OpenAiChatTool ReadFileTool() =>
        new()
        {
            Type = "function",
            Function = new OpenAiChatFunction { Name = "read_file", Description = "Leser en fil" }
        };

    private OpenAiMessage AssistantCall(string callId, string name) =>
        new("assistant", null)
        {
            ToolCalls = [new OpenAiChatToolCall
            {
                Id = callId,
                Function = new OpenAiChatToolFunction { Name = name, Arguments = "{}" }
            }]
        };

    private OpenAiMessage ToolResult(string callId, string output) =>
        new("tool", output) { ToolCallId = callId };

    private IChatProviderRegistry CreateRegistry(IChatProvider provider)
    {
        var aigravityProvider = new FakeChatProvider("Aigravity", new[] { "agy-model" }, "ok");
        var providers = new List<IChatProvider> { aigravityProvider, provider };
        var modelIdCache = new ModelIdCache(new TestRuntimeSettings());

        return new ChatProviderRegistry(providers, modelIdCache, new TestRuntimeSettings(), NullLogger<ChatProviderRegistry>.Instance);
    }
}
