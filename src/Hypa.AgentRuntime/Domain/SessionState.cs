namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Pure session snapshot. Serializable, independent of PTY/VT runtime objects.
/// </summary>
public sealed record SessionState
{
    public required SessionId Id { get; init; }

    /// <summary>Filesystem / UNIQUE session name (maps to <c>sessions.name</c>).</summary>
    public string Name { get; init; } = "default";

    public string LifecycleState { get; init; } = SessionLifecycle.Ready;

    /// <summary>Placement affinity: <c>local</c> default; remote later.</summary>
    public string Placement { get; init; } = "local";

    public int PlacementGeneration { get; init; }

    /// <summary>
    /// Session-level opaque Atomic binding. Pane bindings may override for Step-owned panes.
    /// </summary>
    public AtomicBinding? Binding { get; init; }

    /// <summary>
    /// Process-level tenant anchor (Appendix E.1 / KD-6). Set on first governed/remote admit
    /// even when only a pane-scoped binding is written. Not a full session Binding.
    /// </summary>
    public string? ProcessTenantId { get; init; }

    /// <summary>
    /// Process-level run anchor for the one-governed-Run invariant. Set on first
    /// governed/remote admit (pane or session scoped).
    /// </summary>
    public string? ProcessRunId { get; init; }

    /// <summary>
    /// When true, binding matrix requires governed fields (tenant, run, session, project_root).
    /// Default false for F1 local unmanaged.
    /// </summary>
    public bool Governed { get; init; }

    /// <summary>
    /// True when journal replay finished (or no journal yet). may set false on incomplete footer.
    /// </summary>
    public bool ReplayComplete { get; init; } = true;

    public string? ReplayError { get; init; }

    public WorkspaceId? FocusedWorkspaceId { get; init; }
    public IReadOnlyDictionary<string, WorkspaceState> Workspaces { get; init; }
        = new Dictionary<string, WorkspaceState>();
    public IReadOnlyDictionary<string, TabState> Tabs { get; init; }
        = new Dictionary<string, TabState>();
    public IReadOnlyDictionary<string, PaneState> Panes { get; init; }
        = new Dictionary<string, PaneState>();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;

    /// <summary>Next durable event sequence (event_cursor).</summary>
    public long NextEventSeq { get; init; }

    /// <summary>
    /// Next semantic agent state-change sequence. Distinct from
    /// <see cref="NextEventSeq"/> and metadata report sequences.
    /// </summary>
    public ulong NextAgentStateChangeSeq { get; init; }
}
