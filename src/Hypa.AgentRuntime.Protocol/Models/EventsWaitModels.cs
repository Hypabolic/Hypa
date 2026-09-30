using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// One-shot <c>events.wait</c> params.
/// </summary>
public sealed record EventsWaitParams
{
    [JsonPropertyName("match_event")]
    public EventsWaitMatch? MatchEvent { get; init; }

    /// <summary>
    /// Optional wait budget in milliseconds.
    /// Omit this field to wait until a match, a pane error, or caller cancel.
    /// When the budget elapses with no match, the call fails.
    /// The <c>error_code</c> is <c>timeout</c>.
    /// The message is <c>timed out waiting for event match</c>.
    /// </summary>
    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; init; }
}

/// <summary>
/// Wait match filter.
/// (<c>tag = event</c>, <c>rename_all = snake_case</c>).
/// Current support is <c>pane_agent_status_changed</c> only.
/// </summary>
public sealed record EventsWaitMatch
{
    public const string PaneAgentStatusChanged = "pane_agent_status_changed";

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("agent_status")]
    public string? AgentStatus { get; init; }
}

/// <summary>
/// Successful one-shot wait result.
/// </summary>
public sealed record EventsWaitResult
{
    public const string WaitMatched = "wait_matched";

    [JsonPropertyName("type")]
    public string Type { get; init; } = WaitMatched;

    [JsonPropertyName("event")]
    public EventsWaitMatchedEvent? Event { get; init; }
}

/// <summary>Matched event envelope inside a wait result.</summary>
public sealed record EventsWaitMatchedEvent
{
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("data")]
    public EventsWaitMatchedEventData? Data { get; init; }
}

/// <summary>
/// Matched <c>pane_agent_status_changed</c> payload.
/// <c>occupant_generation</c> is the Hypa pin used to reject a replacement occupant.
/// </summary>
public sealed record EventsWaitMatchedEventData
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("agent_status")]
    public string? AgentStatus { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }
}

/// <summary>Wire <c>error_code</c> values for <c>events.wait</c>.</summary>
public static class EventsWaitErrors
{
    public const string Timeout = "timeout";
    public const string TimeoutMessage = "timed out waiting for event match";
    public const string UnsupportedMatch = "unsupported_event_wait_match";
    public const string UnsupportedMatchMessage =
        "events.wait currently supports pane agent status matches";
    public const string AgentNotRunning = "agent_not_running";
    public const string AgentNotRunningMessage = "agent is no longer running in the target pane";
    public const string PaneNotFound = "pane_not_found";
}
