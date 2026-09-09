namespace AiProxy.Application.Interfaces;

public interface IExecutablePathResolver
{
    string Resolve(string command);
}
