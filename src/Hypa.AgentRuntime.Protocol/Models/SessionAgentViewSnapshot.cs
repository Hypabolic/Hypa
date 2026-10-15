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

    /// <summary>Program status summary of the session. Absent when no pane has records.</summary>
    [JsonPropertyName("session")]
    public System.Text.Json.JsonElement? Session { get; init; }

    /// <summary>Program status view of each pane that has records. Each item has <c>pane_id</c>.</summary>
    [JsonPropertyName("panes")]
    public IReadOnlyList<System.Text.Json.JsonElement>? Panes { get; init; }

    /// <summary>Program status view of each tab that has a pane with records.</summary>
    [JsonPropertyName("tabs")]
    public IReadOnlyList<System.Text.Json.JsonElement>? Tabs { get; init; }
}
