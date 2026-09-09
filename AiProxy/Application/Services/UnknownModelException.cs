namespace AiProxy.Application.Services;

public sealed class UnknownModelException : Exception
{
    public UnknownModelException(string message) : base(message)
    {
    }
}
