using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>runtime.handoff.export</c> params. Controller lease required.</summary>
public sealed record HandoffExportParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

/// <summary><c>runtime.handoff.export</c> result.</summary>
public sealed record HandoffExportResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("handoff_path")]
    public string? HandoffPath { get; init; }

    [JsonPropertyName("nonce")]
    public string? Nonce { get; init; }

    [JsonPropertyName("generation")]
    public int Generation { get; init; }
}

/// <summary><c>runtime.handoff.adopt</c> params. Controller lease required.</summary>
public sealed record HandoffAdoptParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("handoff_path")]
    public string? HandoffPath { get; init; }

    [JsonPropertyName("nonce")]
    public string? Nonce { get; init; }

    [JsonPropertyName("generation")]
    public int? Generation { get; init; }
}

/// <summary><c>runtime.handoff.adopt</c> result.</summary>
public sealed record HandoffAdoptResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("adopted")]
    public bool Adopted { get; init; }

    [JsonPropertyName("child_pid")]
    public int? ChildPid { get; init; }
}
