using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// Params of the <c>render.ack</c> notification. The client sends one after
/// each host paint. One paint can show frames of more than one pane, so the
/// notification has one entry for each pane. Each entry acks every render
/// frame (cells or snapshot slice) of its pane up to and including
/// <see cref="RenderAckEntry.Generation"/>. The ack only gives credit back to
/// the mux. It does not change the delta base, and it does not name the
/// focused pane: the mux takes the focus from the input of the connection.
/// </summary>
public sealed record RenderAckParams
{
    [JsonPropertyName("acks")]
    public List<RenderAckEntry>? Acks { get; init; }
}

/// <summary>The highest render generation of one pane that the client painted or dropped.</summary>
public sealed record RenderAckEntry
{
    [JsonPropertyName("pane_id")]
    public required string PaneId { get; init; }

    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    /// <summary>
    /// The end row (exclusive) of the last line of <see cref="Generation"/>
    /// that the client read: for a cells line, one more than its highest row
    /// index; for a snapshot slice, its <c>row_end</c>. The ack then covers
    /// only the slices of that generation up to this row. Null covers every
    /// line of the generation.
    /// </summary>
    [JsonPropertyName("row_end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RowEnd { get; init; }
}
