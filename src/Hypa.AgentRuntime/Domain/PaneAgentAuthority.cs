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
}
