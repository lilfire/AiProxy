namespace AiProxy.Services;

/// <summary>
/// Holder siste todo-øyeblikksbilde fra provideren innenfor én forespørsel.
/// Må registreres som Scoped: et delt lager ville lekket todo-lister mellom brukere.
/// </summary>
public interface ITodoSnapshotStore
{
    TodoSnapshot? Snapshot { get; }

    void Capture(TodoSnapshot snapshot);

    void Clear();
}
