namespace AiProxy.Services;

public sealed class ImageInputException(string message) : Exception(message);

public sealed class ImageInputNotSupportedException(string message) : Exception(message);
