namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Pane lifecycle states stored in SQLite and used for restart reconciliation.
/// </summary>
public static class PaneLifecycle
{
    public const string Created = "created";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Orphaned = "orphaned";
    public const string Closed = "closed";
}
