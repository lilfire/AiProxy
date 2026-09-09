using AiProxy.Services.Sessions;

namespace AiProxy.Application.Interfaces;

public interface ISessionHistoryStore
{
    string StartTurn(string sessionId, string api, string model, IReadOnlyList<SessionHistoryMessage> input);
    void SetProvider(string turnId, string provider, string model);
    void CompleteTurn(string turnId, string output);
    void FailTurn(string turnId, Exception exception);
    IReadOnlyList<SessionHistorySession> GetSnapshot();
    IDisposable Subscribe(Action<SessionHistoryChanged> listener);
}
