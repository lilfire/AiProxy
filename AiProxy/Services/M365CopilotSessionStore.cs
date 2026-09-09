using System.Collections.Concurrent;

namespace AiProxy.Services;

public sealed class M365CopilotSessionStore
{
    private readonly ConcurrentDictionary<string, M365CopilotConversation> _conversations = new();

    public M365CopilotTurn GetNextTurn(string clientSessionId)
    {
        var conversation = _conversations.GetOrAdd(clientSessionId, _ => new M365CopilotConversation(Guid.NewGuid().ToString()));
        var turnNumber = Interlocked.Increment(ref conversation.TurnCount) - 1;

        return new M365CopilotTurn(conversation.ConversationId, turnNumber == 0);
    }
}

public sealed class M365CopilotConversation
{
    public M365CopilotConversation(string conversationId) => ConversationId = conversationId;

    public string ConversationId { get; }
    public int TurnCount;
}

public sealed record M365CopilotTurn(string ConversationId, bool IsFirstTurn);
