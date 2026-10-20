using System.Text.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// The client side of render credit. After each host paint, the render loop
/// acks the cells frames of that paint on the connection that sent them. A
/// cells frame or a snapshot slice that the loop takes and does not paint is
/// acked too, so its bytes do not hold the credit of the connection. The ack
/// does not name the focused pane. The mux takes the focus from the input of
/// the connection.
/// </summary>
public sealed partial class AttachSession
{
    /// <summary>Acks the frames of one cells batch after its host write.</summary>
    internal static void AckPaintedCells(
        ControlPlaneClient reader,
        AttachLiveState live,
        IReadOnlyList<TerminalRenderCellsPayload> batch)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(batch);
        if (!reader.RenderCreditEnabled || batch.Count == 0)
            return;
        var painted = new List<RenderAck>(batch.Count);
        foreach (var cells in batch)
        {
            var paneId = cells.PaneId ?? live.PaneId;
            if (!string.IsNullOrEmpty(paneId) && cells.Generation > 0)
                painted.Add(new RenderAck(paneId, cells.Generation, RenderRows.CellsRowEnd(cells)));
        }

        reader.NoteRenderPainted(painted);
    }

    /// <summary>
    /// Acks one cells frame or one snapshot slice that the render loop took.
    /// A snapshot slice is acked when it arrives, not when its batch is
    /// complete: the mux sends the next slice only after credit comes back.
    /// The ack carries the end row of the line, so it gives back only the
    /// slices of the frame that the client read.
    /// </summary>
    internal static void AckConsumedRender(ControlPlaneClient reader, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!reader.RenderCreditEnabled
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !(kind.ValueEquals(TerminalRenderCellsPayload.KindCells)
                || kind.ValueEquals(TerminalRenderSnapshotPayload.KindSnapshot))
            || !payload.TryGetProperty("pane_id", out var pane)
            || pane.ValueKind != JsonValueKind.String
            || pane.GetString() is not { Length: > 0 } paneId
            || !payload.TryGetProperty("generation", out var generationEl)
            || generationEl.ValueKind != JsonValueKind.Number
            || !generationEl.TryGetInt64(out var generation)
            || generation <= 0)
        {
            return;
        }

        var rowEnd = kind.ValueEquals(TerminalRenderSnapshotPayload.KindSnapshot)
            ? payload.TryGetProperty("row_end", out var end) && end.TryGetInt32(out var snapshotEnd)
                ? snapshotEnd
                : RenderRows.All
            : RenderRows.CellsRowEnd(payload);
        reader.NoteRenderPainted([new RenderAck(paneId, generation, rowEnd)]);
    }
}
