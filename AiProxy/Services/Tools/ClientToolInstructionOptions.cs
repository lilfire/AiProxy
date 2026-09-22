namespace AiProxy.Services.Tools;

/// <summary>
/// Avsnitt en provider legger til i den generiske verktøyinstruksjonen. Avsnittene settes inn
/// mellom innledningen og gjerdeformatet, slik at provider-spesifikke regler ikke lekker til
/// de andre providerne.
/// </summary>
public sealed record ClientToolInstructionOptions
{
    public IReadOnlyList<string> ExtraParagraphs { get; init; } = [];
}
