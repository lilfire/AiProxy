namespace AiProxy.Services;

/// <summary>
/// En todo-oppgave hentet ut av en provider-strøm. Feltene er materialiserte strenger,
/// aldri JsonElement, slik at øyeblikksbildet er trygt å bruke etter at strømmen er ferdig.
/// </summary>
public sealed record TodoSnapshotItem(string Content, string Status);
