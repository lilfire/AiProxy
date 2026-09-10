using System.Net.WebSockets;
using System.Text;
using AiProxy.Services;
using AiProxy.Services.Providers;

namespace AiProxy.Tests;

[TestClass]
public sealed class M365CopilotResponseTests
{
    [TestMethod]
    public async Task Final_internal_error_is_not_returned_as_an_assistant_apology()
    {
        using var socket = new FrameSocket("""
            {"type":2,"item":{"result":{"value":"InternalError","message":"Sorry, I wasn't able to respond to that."},"messages":[{"author":"bot","contentOrigin":"BotConnection","text":"Sorry, I wasn't able to respond to that."}]}}
            """);
        var chunks = new List<string>();

        var error = await Assert.ThrowsExceptionAsync<M365CopilotUpstreamException>(() =>
            M365CopilotProvider.ReadResponseAsync(socket, (text, _) => { chunks.Add(text); return Task.CompletedTask; }, CancellationToken.None, "request-test"));

        Assert.AreEqual("InternalError", error.Code);
        Assert.AreEqual("request-test", error.RequestId);
        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(7)]
    public async Task Invocation_and_close_errors_are_not_swallowed(int type)
    {
        using var socket = new FrameSocket($$"""{"type":{{type}},"error":"Failed to invoke 'Chat' due to an error on the server."}""");

        var error = await Assert.ThrowsExceptionAsync<M365CopilotUpstreamException>(() => ReadAsync(socket));

        Assert.AreEqual("InvocationError", error.Code);
    }

    [TestMethod]
    public async Task Successful_final_snapshot_can_supply_the_entire_answer()
    {
        using var socket = new FrameSocket("""
            {"type":2,"item":{"result":{"value":"Success"},"messages":[{"author":"bot","text":"2"}]}}
            """);

        Assert.AreEqual("2", await ReadAsync(socket));
    }

    [TestMethod]
    public async Task Mixed_deltas_and_snapshots_do_not_duplicate_text_or_include_progress()
    {
        using var socket = new FrameSocket(
            """{"type":6}""",
            """{"type":1,"target":"update","arguments":[{"messages":[{"author":"bot","messageType":"Progress","text":"Looking into it..."},{"author":"bot","text":"1 + "}]}]}""",
            """{"type":1,"target":"update","arguments":[{"writeAtCursor":"1 = 2"}]}""",
            """{"type":2,"item":{"result":{"value":"Success"},"messages":[{"author":"bot","text":"1 + 1 = 2"}]}}""");

        Assert.AreEqual("1 + 1 = 2", await ReadAsync(socket));
        Assert.AreEqual(1, socket.Sent.Count);
        StringAssert.Contains(socket.Sent[0], "\"type\":6");
    }

    [TestMethod]
    public async Task Final_failure_overrides_partial_output()
    {
        using var socket = new FrameSocket(
            """{"type":1,"target":"update","arguments":[{"writeAtCursor":"partial"}]}""",
            """{"type":2,"item":{"result":{"value":"InternalError"}}}""");

        var error = await Assert.ThrowsExceptionAsync<M365CopilotUpstreamException>(() => ReadAsync(socket));

        Assert.AreEqual("InternalError", error.Code);
    }

    [TestMethod]
    public async Task Empty_completion_is_an_error()
    {
        using var socket = new FrameSocket("""{"type":2,"item":{"result":{"value":"Success"}}}""");

        var error = await Assert.ThrowsExceptionAsync<M365CopilotUpstreamException>(() => ReadAsync(socket));

        Assert.AreEqual("EmptyResponse", error.Code);
    }

    [TestMethod]
    public async Task Connection_closed_before_completion_does_not_succeed_with_partial_output()
    {
        using var socket = new FrameSocket("""{"type":1,"target":"update","arguments":[{"writeAtCursor":"partial"}]}""");

        var error = await Assert.ThrowsExceptionAsync<M365CopilotUpstreamException>(() => ReadAsync(socket));

        Assert.AreEqual("ConnectionClosed", error.Code);
    }

    private static Task<string> ReadAsync(WebSocket socket) =>
        M365CopilotProvider.ReadResponseAsync(socket, null, CancellationToken.None, "request-test");

    private sealed class FrameSocket(params string[] frames) : WebSocket
    {
        private readonly Queue<byte[]> _frames = new(frames.Select(frame => Encoding.UTF8.GetBytes(frame + '\u001e')));
        public List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_frames.TryDequeue(out var frame))
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            frame.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.AsSpan()));
            return Task.CompletedTask;
        }
    }
}
