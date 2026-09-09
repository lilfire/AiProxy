namespace AiProxy.Application.Interfaces;

public interface ISessionIdResolver
{
    string ResolveSessionId(HttpContext context);
}
