using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>Transient Agents-view fields on <c>session.snapshot</c>.</summary>
public sealed record SessionAgentViewSnapshot
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("has_sort")]
    public bool HasSort { get; init; }
}
