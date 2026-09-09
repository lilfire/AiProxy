using AiProxy.Contracts;

namespace AiProxy.Services;

public class ModelResolution
{
    public IChatProvider Provider { get; set; } = null!;
    public string ModelId { get; set; } = string.Empty;
    public string DisplayModelId { get; set; } = string.Empty;
}
