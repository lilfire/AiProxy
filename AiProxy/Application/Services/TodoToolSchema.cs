namespace AiProxy.Application.Services;

/// <summary>
/// Hva klienten faktisk deklarerte om sitt todo-verktøy. Leses ut av forespørselen før
/// strømmingen starter, slik at ingen JsonElement lever forbi requesten.
/// </summary>
public sealed record TodoToolSchema(bool IsDeclared, string DeclaredName, bool SupportsId, bool SupportsPriority);
