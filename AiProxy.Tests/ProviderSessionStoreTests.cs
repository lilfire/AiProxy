using AiProxy.Services;

namespace AiProxy.Tests;

[TestClass]
public class ProviderSessionStoreTests
{
    [TestMethod]
    public void GetOrCreateSessionId_creates_new_id_when_none_exists()
    {
        var store = new ProviderSessionStore();

        var result = store.GetOrCreateSessionId("session-1", "ProviderA", () => "provider-session-1");

        Assert.IsTrue(result.WasCreated);
        Assert.AreEqual("provider-session-1", result.ProviderSessionId);
    }

    [TestMethod]
    public void GetOrCreateSessionId_returns_existing_id_without_creating()
    {
        var store = new ProviderSessionStore();
        store.GetOrCreateSessionId("session-1", "ProviderA", () => "provider-session-1");

        var result = store.GetOrCreateSessionId("session-1", "ProviderA", () => "should-not-be-used");

        Assert.IsFalse(result.WasCreated);
        Assert.AreEqual("provider-session-1", result.ProviderSessionId);
    }

    [TestMethod]
    public void HasSession_returns_true_when_session_exists()
    {
        var store = new ProviderSessionStore();
        store.GetOrCreateSessionId("session-1", "ProviderA", () => "provider-session-1");

        var result = store.HasSession("session-1", "ProviderA");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void HasSession_returns_false_when_session_does_not_exist()
    {
        var store = new ProviderSessionStore();

        var result = store.HasSession("session-1", "ProviderA");

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Reset_removes_session()
    {
        var store = new ProviderSessionStore();
        store.GetOrCreateSessionId("session-1", "ProviderA", () => "provider-session-1");

        store.Reset("session-1", "ProviderA");

        Assert.IsFalse(store.HasSession("session-1", "ProviderA"));
    }

    [TestMethod]
    public void M365_sessions_are_separate_for_different_client_session_ids()
    {
        var store = new M365CopilotSessionStore();

        var first = store.GetNextTurn("ses-one");
        var second = store.GetNextTurn("ses-two");

        Assert.IsTrue(first.IsFirstTurn);
        Assert.IsTrue(second.IsFirstTurn);
        Assert.AreNotEqual(first.ConversationId, second.ConversationId);
    }
}
