using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>pane.report_agent</c> params. Semantic state owns waits and notify.</summary>
public sealed record PaneReportAgentParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }

    [JsonPropertyName("agent_session_id")]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("agent_session_path")]
    public string? AgentSessionPath { get; init; }

    [JsonPropertyName("session_start_source")]
    public string? SessionStartSource { get; init; }
}

/// <summary><c>pane.report_agent_session</c> params. Does not change waits.</summary>
public sealed record PaneReportAgentSessionParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("agent_session_id")]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("agent_session_path")]
    public string? AgentSessionPath { get; init; }

    [JsonPropertyName("session_start_source")]
    public string? SessionStartSource { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }
}

/// <summary><c>pane.report_metadata</c> params. Same contract as workspace.report_metadata plus pane_id.</summary>
public sealed record PaneReportMetadataParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("tokens")]
    public IReadOnlyDictionary<string, string?>? Tokens { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }

    [JsonPropertyName("ttl_ms")]
    public int? TtlMs { get; init; }
}

/// <summary><c>pane.release_agent</c> params. Matching source+agent ends authority.</summary>
public sealed record PaneReleaseAgentParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }
}

/// <summary><c>pane.clear_agent_authority</c> params. A non-owner source fails closed.</summary>
public sealed record PaneClearAgentAuthorityParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }
}

/// <summary>Native agent-session reference on pane/agent objects.</summary>
public sealed record NativeAgentSessionDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("session_start_source")]
    public string? SessionStartSource { get; init; }
}

/// <summary>Ok result for report / release / clear authority methods.</summary>
public sealed record AgentAuthorityResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}
