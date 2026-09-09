namespace AiProxy.Contracts;

public class ModelConfig
{
    public ModelConfig(string id, string name, string providerName)
    {
        Id = id;
        Name = name;
        ProviderName = providerName;
    }

    public string Id { get; init; }
    public string Name { get; init; }
    public string ProviderName { get; init; }
}
