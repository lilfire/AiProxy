namespace AiProxy.Services;

public sealed class TodoSnapshotStore : ITodoSnapshotStore
{
    private TodoSnapshot? _snapshot;

    public TodoSnapshot? Snapshot => _snapshot;

    public void Capture(TodoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
    }

    public void Clear()
    {
        _snapshot = null;
    }
}
