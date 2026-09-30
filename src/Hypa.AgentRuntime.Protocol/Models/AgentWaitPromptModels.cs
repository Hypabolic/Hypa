using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Protocol.Json;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>agent.wait</c> params — pinned to occupant generation on the server.</summary>
public sealed record AgentWaitParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("until")]
    public string? Until { get; init; }

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; init; }
}

/// <summary>Optional wait object nested under <c>agent.prompt</c>.</summary>
public sealed record AgentWaitSpec
{
    [JsonPropertyName("until")]
    public string? Until { get; init; }

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; init; }
}

/// <summary><c>agent.prompt</c> params with optional atomic wait object.</summary>
public sealed record AgentPromptParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Optional input lease id. Required when the connection does not already hold
    /// an active input lease for the pane.
    /// </summary>
    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    /// <summary>Legacy top-level wait target when <c>wait</c> is a bool (until protocol major 2).</summary>
    [JsonPropertyName("until")]
    public string? Until { get; init; }

    /// <summary>Legacy top-level wait budget when <c>wait</c> is a bool (until protocol major 2).</summary>
    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; init; }

    [JsonPropertyName("wait")]
    [JsonConverter(typeof(AgentPromptWaitJsonConverter))]
    public AgentWaitSpec? Wait { get; init; }
}

/// <summary>Wait outcome embedded in agent wait/prompt results.</summary>
public sealed record AgentWaitOutcome
{
    [JsonPropertyName("until")]
    public string? Until { get; init; }

    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; init; }
}

/// <summary>
/// Shared agent status result for <c>agent.wait</c> / <c>agent.prompt</c> (B.6),
/// including P0 occupant pinning fields.
/// </summary>
public sealed record AgentStatusResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("alive")]
    public bool Alive { get; init; }

    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int? OccupantGeneration { get; init; }

    [JsonPropertyName("occupant_id")]
    public string? OccupantId { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public string? RuntimeSessionId { get; init; }

    [JsonPropertyName("wait")]
    public AgentWaitOutcome? Wait { get; init; }

    [JsonPropertyName("agent_session")]
    public NativeAgentSessionDto? AgentSession { get; init; }

    [JsonPropertyName("tokens")]
    public IReadOnlyDictionary<string, string>? Tokens { get; init; }
}
