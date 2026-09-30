namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Pure pane data — no PTY, no VT, no I/O. Testable without spawning processes.
/// </summary>
public sealed record PaneState
{
    public required PaneId Id { get; init; }
    public required TabId TabId { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    /// <summary>Tiled (layout leaf) or hidden (off-layout occupant). Default tiled.</summary>
    public PanePlacement Placement { get; init; } = PanePlacement.Tiled;
    /// <summary>Validated parent pane id. Not a secret. Null when create had no proof.</summary>
    public PaneId? ParentPaneId { get; init; }
    public string Label { get; init; } = string.Empty;
    /// <summary>
    /// Unique live agent name. Distinct from <see cref="Label"/>.
    /// (<c>src/app/agents.rs</c> <c>rename_agent_target</c>).
    /// </summary>
    public string? AgentName { get; init; }
    public string Cwd { get; init; } = Environment.CurrentDirectory;
    public string Command { get; init; } = string.Empty;
    /// <summary>Argv tail after <see cref="Command"/> (argv0). Persisted as panes.args_json and on the layout leaf.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 40;
    public AgentStatus AgentStatus { get; init; } = AgentStatus.Unknown;
    public string? AgentKind { get; init; }
    public string? AgentMessage { get; init; }
    public AtomicBinding? Binding { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsAlive { get; init; }
    public int? ExitCode { get; init; }

    /// <summary>Lifecycle for persistence / restart (created|starting|running|exited|orphaned|closed).</summary>
    public string LifecycleState { get; init; } = PaneLifecycle.Created;

    /// <summary>
    /// Occupant generation counter. First occupant is admitted as 1 at pane register;
    /// 0 is only a pre-admit / unset sentinel and is never a valid pin target.
    /// </summary>
    public int OccupantGeneration { get; init; }

    /// <summary>Continuity Work id on this occupant. Null when the occupant is not a Work.</summary>
    public string? WorkId { get; init; }

    /// <summary>Continuity Work generation on this occupant. 0 when <see cref="WorkId"/> is null.</summary>
    public long WorkGeneration { get; init; }

    /// <summary>Occupant HOME used at spawn. Null for a pane that is not an occupant.</summary>
    public string? Home { get; init; }

    /// <summary>Last known OS process id; null after exit/orphan reconciliation.</summary>
    public int? Pid { get; init; }

    /// <summary>
    /// False while a done occupant is unreviewed. Focus promotes done to idle
    /// and sets seen. New panes default seen because they are not unreviewed done.
    /// </summary>
    public bool Seen { get; init; } = true;

    /// <summary>
    /// Semantic agent state-change sequence. Bumped when
    /// <see cref="AgentStatus"/> changes. Distinct from metadata report seq.
    /// </summary>
    public ulong? LastAgentStateChangeSeq { get; init; }

    /// <summary>Right-click policy on the Hypa wire: <c>hypa</c> or <c>pane</c>. Default <c>hypa</c>.</summary>
    public string RightClick { get; init; } = PaneRightClick.Hypa;

    /// <summary>Semantic report owner. Detector does not overwrite status while this is set.</summary>
    public PaneAgentAuthority? AgentAuthority { get; init; }

    /// <summary>True while <see cref="AgentAuthority"/> owns waits, notify, and rollups.</summary>
    public bool HoldsSemanticAuthority => AgentAuthority is not null;

    /// <summary>
    /// Status after the occupant process dies or a host stops. Held authority keeps
    /// <see cref="PaneAgentAuthority.State"/>; otherwise working/unknown becomes done.
    /// </summary>
    public AgentStatus StatusAfterProcessDeath() =>
        AgentAuthority is { } held
            ? held.State
            : AgentStatus is AgentStatus.Working or AgentStatus.Unknown
                ? AgentStatus.Done
                : AgentStatus;

    /// <summary>Native session reference. Independent of waits.</summary>
    public NativeAgentSessionRef? AgentSession { get; init; }

    /// <summary>
    /// Last accepted authority sequence per source. Shared by report/session/release/clear.
    /// Distinct from metadata token sequences.
    /// </summary>
    public IReadOnlyDictionary<string, long> AgentAuthoritySequences { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
}
