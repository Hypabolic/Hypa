namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Semantic pane authority from <c>pane.report_agent</c>. Durable. Detector does not
/// overwrite status while this is set.
/// </summary>
public sealed record PaneAgentAuthority
{
    public required string Source { get; init; }
    public required string Agent { get; init; }
    public required AgentStatus State { get; init; }
    public string? Message { get; init; }
    public long? Sequence { get; init; }
    public string? Session { get; init; }
    public DateTimeOffset ReportedAt { get; init; }

    /// <summary>
    /// Program status only. The kind the pane had before the records took the status.
    /// The pane gets it back when the last record goes. Runtime state, like the authority.
    /// </summary>
    public string? PriorAgentKind { get; init; }

    /// <summary>Rank of <see cref="Source"/> in <see cref="AgentSourcePrecedence"/>.</summary>
    public int Rank => AgentSourcePrecedence.Rank(Source);

    /// <summary>
    /// True for an authority that comes from program status records.
    /// It lives as long as the records. It is derived state: the pane saves the records,
    /// not this authority. A restore computes it again.
    /// </summary>
    public bool IsProgramStatus => Rank == AgentSourcePrecedence.ProgramStatusRank;
}
