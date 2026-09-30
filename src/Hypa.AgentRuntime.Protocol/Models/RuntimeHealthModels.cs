using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>runtime.health</c> result (B.1 / B.6). Capabilities default empty for minimal fixtures.</summary>
public sealed record RuntimeHealthResult
{
    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public string? RuntimeSessionId { get; init; }

    [JsonPropertyName("protocol_major")]
    public int ProtocolMajor { get; init; }

    [JsonPropertyName("protocol_minor")]
    public int ProtocolMinor { get; init; }

    [JsonPropertyName("persistence_schema")]
    public int PersistenceSchema { get; init; }

    [JsonPropertyName("journal")]
    public JournalHealthDto? Journal { get; init; }

    [JsonPropertyName("pty")]
    public PtyHealthDto? Pty { get; init; }

    [JsonPropertyName("vt")]
    public VtHealthDto? Vt { get; init; }

    [JsonPropertyName("limits")]
    public LimitsHealthDto? Limits { get; init; }

    /// <summary>Advertised capability tokens; empty when omitted in minimal fixtures.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }
}

public sealed record JournalHealthDto
{
    [JsonPropertyName("next_seq")]
    public long NextSeq { get; init; }

    [JsonPropertyName("replay_complete")]
    public bool ReplayComplete { get; init; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>
    /// Present when <see cref="ReplayComplete"/> is false (incomplete footer/checksum recovery).
    /// Null when replay completed cleanly.
    /// </summary>
    [JsonPropertyName("replay_error")]
    public string? ReplayError { get; init; }

    /// <summary>Lowest sequence replay can still serve.</summary>
    [JsonPropertyName("floor_seq")]
    public long FloorSeq { get; init; }

    /// <summary>Reliable appends the journal refused.</summary>
    [JsonPropertyName("refusal_count")]
    public int RefusalCount { get; init; }

    /// <summary>Reason from the latest refused reliable append.</summary>
    [JsonPropertyName("last_refusal_reason")]
    public string? LastRefusalReason { get; init; }
}

public sealed record PtyHealthDto
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("interactive")]
    public bool Interactive { get; init; }
}

public sealed record VtHealthDto
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>
    /// Default pane / floor columns (not max over live panes). Null in minimal fixtures.
    /// </summary>
    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    /// <summary>
    /// Default pane / floor rows (not max over live panes). Null in minimal fixtures.
    /// </summary>
    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    /// <summary>
    /// Ghostty version string when <see cref="Provider"/> is <c>ghostty</c>.
    /// Null for Basic default health.
    /// </summary>
    [JsonPropertyName("ghostty_version")]
    public string? GhosttyVersion { get; init; }

    /// <summary>Ghostty build metadata (commit) when loaded.</summary>
    [JsonPropertyName("ghostty_build")]
    public string? GhosttyBuild { get; init; }

    /// <summary>Hypa abi-manifest version for the loaded Ghostty surface.</summary>
    [JsonPropertyName("abi")]
    public string? Abi { get; init; }

    /// <summary>
    /// Why Basic is active instead of Ghostty (never silent when required_ghostty).
    /// Null when provider is healthy or never attempted.
    /// </summary>
    [JsonPropertyName("fallback_reason")]
    public string? FallbackReason { get; init; }
}

public sealed record LimitsHealthDto
{
    [JsonPropertyName("max_panes")]
    public int MaxPanes { get; init; }

    [JsonPropertyName("max_attachments")]
    public int MaxAttachments { get; init; }
}

/// <summary>Empty params object for methods that take <c>{}</c>.</summary>
public sealed record EmptyParams;

/// <summary><c>server.stop</c> result. Written before host shutdown.</summary>
public sealed record ServerStopResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary>
/// <c>server.live_handoff</c> result. Unix remote-server replacement.
/// This does not move Work.
/// </summary>
public sealed record ServerLiveHandoffResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}
