using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>pane.send_keys</c> params — exact key bytes. Lease required
/// for callers that are not an active ClientShell viewer of the pane.</summary>
public sealed record PaneSendKeysParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("overlay_generation")]
    public long? OverlayGeneration { get; init; }
}

/// <summary><c>pane.send_keys</c> result.</summary>
public sealed record PaneSendKeysResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("accepted_bytes")]
    public int AcceptedBytes { get; init; }
}
