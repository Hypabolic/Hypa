using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// Opaque Atomic-facing binding. Hypa stores and exports these strings only;
/// it never mutates Atomic Run/Step state and exposes no <c>atomic.*</c> methods.
/// </summary>
public sealed record BindingDto
{
    [JsonPropertyName("agent_session_id")]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("step_id")]
    public string? StepId { get; init; }

    [JsonPropertyName("memory_id")]
    public string? MemoryId { get; init; }

    [JsonPropertyName("project_root")]
    public string? ProjectRoot { get; init; }

    /// <summary>Opaque tenant claim. Required for governed/remote; not inferred.</summary>
    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }
}
