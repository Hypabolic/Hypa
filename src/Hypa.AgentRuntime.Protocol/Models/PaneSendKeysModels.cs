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

    /// <summary>
    /// Optional input sequence, monotonic for one client and one pane. A
    /// client sends it only when the endpoint welcome lists
    /// <c>hypa.attach.input_ack.v2</c>. The mux returns the highest written
    /// value as <c>input_ack</c> on the live cells bodies for that client.
    /// </summary>
    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seq { get; init; }

    /// <summary>
    /// Optional. True when the bytes are key presses or typed text. False for
    /// mouse reports, focus reports and bracketed paste. Only keyboard input
    /// opens the echo window of the pane. A missing field means true, so an
    /// older client keeps its behaviour.
    /// </summary>
    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Keyboard { get; init; }
}

/// <summary><c>pane.send_keys</c> result.</summary>
public sealed record PaneSendKeysResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("accepted_bytes")]
    public int AcceptedBytes { get; init; }
}
