namespace Hypa.AgentRuntime.Application;

/// <summary>
// / File persist for pane screen history.
/// <c>src/persist/io.rs:14-16</c> <c>session-history.json</c>.
/// </summary>
public interface IPaneHistorySnapshotStore
{
    SessionHistorySnapshot? Load();

    RuntimeResult<RuntimeUnit> Save(SessionHistorySnapshot snapshot);

    RuntimeResult<RuntimeUnit> Clear();
}

/// <summary>No file. Tests and hosts that do not persist history.</summary>
public sealed class NullPaneHistorySnapshotStore : IPaneHistorySnapshotStore
{
    public static NullPaneHistorySnapshotStore Instance { get; } = new();

    public SessionHistorySnapshot? Load() => null;

    public RuntimeResult<RuntimeUnit> Save(SessionHistorySnapshot snapshot)
    {
        _ = snapshot;
        return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
    }

    public RuntimeResult<RuntimeUnit> Clear() =>
        RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
}
