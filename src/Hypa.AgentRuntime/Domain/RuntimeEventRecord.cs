namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// One durable runtime event as stored in a HYJR segment and projected to subscribers.
/// Payload is canonical UTF-8 JSON.
/// </summary>
public sealed record RuntimeEventRecord
{
    public required long Seq { get; init; }
    public required EventClass Class { get; init; }
    public required EventReliability Reliability { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    /// <summary>UTF-8 JSON object for the event payload (not the full runtime.event envelope).</summary>
    public required string PayloadJson { get; init; }

    /// <summary>
    /// Live emit UTF-8 JSON. When set, the formatter splices these bytes
    /// and does not recode <see cref="PayloadJson"/>.
    /// </summary>
    public byte[]? PayloadUtf8 { get; init; }

    /// <summary>
    /// True when a source-generated JSON writer produced
    /// <see cref="PayloadUtf8"/>. The formatter then splices those bytes
    /// without a validating read. Only a mux producer may set this flag.
    /// A payload from a plugin, a file, or a socket must leave it false.
    /// </summary>
    public bool PayloadUtf8Trusted { get; init; }

    /// <summary>
    /// Producer-side pane routing hint. Live Output sets this so the hub
    /// does not parse <see cref="PayloadJson"/>. Replay records leave it
    /// null and keep the parse path.
    /// </summary>
    public string? PaneKey { get; init; }
    /// <summary>Optional subscription id when projecting to a specific observer (live path).</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>
    /// Live-queue pin group. Snapshot slices that share a group enqueue and
    /// drop as one unit. Null means an ordinary droppable record.
    /// </summary>
    public string? LivePinGroup { get; init; }

    /// <summary>
    /// Writer lane chosen from typed envelope fields. control, ordered, or render.
    /// </summary>
    public string? Lane { get; init; }

    /// <summary>Pane whose frame identity commits after writer admission.</summary>
    public string? FramePaneId { get; init; }

    /// <summary>Semantic identity committed only after writer-lane admission.</summary>
    public string? FrameIdentity { get; init; }

    /// <summary>Attach surface revision stamped at admit for enqueue recheck.</summary>
    public ulong? AttachEmitSurfaceRevision { get; init; }

    /// <summary>Attach snapshot revision stamped at admit for enqueue recheck.</summary>
    public ulong? AttachEmitSnapshotRevision { get; init; }

    /// <summary>Mux boot id stamped onto live <c>terminal.render</c> params.</summary>
    public string? AttachEmitBootId { get; init; }

    /// <summary>Projection floor stamped onto live <c>terminal.render</c> params.</summary>
    public ulong? AttachEmitProjectionRevision { get; init; }

    /// <summary>Hello columns stamped onto live <c>terminal.render</c> params.</summary>
    public ushort? AttachEmitColumns { get; init; }

    /// <summary>Hello rows stamped onto live <c>terminal.render</c> params.</summary>
    public ushort? AttachEmitRows { get; init; }

    /// <summary>Viewer focus stamp on live <c>terminal.render</c> params.</summary>
    public bool? AttachEmitFocused { get; init; }

    /// <summary>Focused pane stamp on live <c>terminal.render</c> params.</summary>
    public string? AttachEmitFocusedPaneId { get; init; }
}

/// <summary>
/// Journal health surface for <c>runtime.health</c> and session snapshot flags.
/// </summary>
public sealed record JournalHealth
{
    /// <summary>Next sequence the allocator will assign (exclusive end of committed range).</summary>
    public required long NextSeq { get; init; }

    /// <summary>False when recovery found an incomplete/mismatched footer.</summary>
    public required bool ReplayComplete { get; init; }

    /// <summary>Total durable journal bytes under the state root (all segments).</summary>
    public required long Bytes { get; init; }

    /// <summary>Optional recovery error detail when <see cref="ReplayComplete"/> is false.</summary>
    public string? ReplayError { get; init; }

    /// <summary>Lowest sequence replay can still serve. Zero means the start of the journal.</summary>
    public long FloorSeq { get; init; }

    /// <summary>Reliable appends the journal refused.</summary>
    public int RefusalCount { get; init; }

    /// <summary>Reason from the latest refused reliable append.</summary>
    public string? LastRefusalReason { get; init; }
}
