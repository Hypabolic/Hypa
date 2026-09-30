using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Bounds a live <c>kind=cells</c> NDJSON line to the 1 MiB socket cap.
/// <c>src/protocol/wire.rs:930-946</c> rejects an oversized frame without
/// panicking. Hypa NDJSON cannot grow past that cap
/// (<see cref="AttachSnapshotPacker.MaxNdjsonLineBytes"/>). Slice by row
/// like <see cref="AttachSnapshotPacker"/>, or drop the frame. A dropped
/// frame is acceptable. A dead attach session is not.
/// </summary>
public static class AttachCellsLinePacker
{
    public const int MaxNdjsonLineBytes = AttachSnapshotPacker.MaxNdjsonLineBytes;

    /// <summary>
    /// A cells payload that chains on a base generation is order dependent.
    /// only after the write. A replaceable slot would break that chain.
    /// </summary>
    public static bool IsOrderedCellsPayload(TerminalRenderCellsPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Full || payload.Reanchor || payload.BaseGeneration > 0;
    }

    public static int Utf8LineBytes(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Encoding.UTF8.GetByteCount(line) + 1;
    }

    public static bool FitsNdjsonLine(string line, int maxLineBytes = MaxNdjsonLineBytes)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        return Utf8LineBytes(line) <= maxLineBytes;
    }

    public static bool FitsNdjsonLine(ReadOnlySpan<byte> utf8, int maxLineBytes = MaxNdjsonLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        return utf8.Length + 1 <= maxLineBytes;
    }

    /// <summary>
    /// Slice <paramref name="payload"/> by row so each formatted NDJSON line
    /// fits <paramref name="maxLineBytes"/>. Empty means drop the frame:
    /// even one row exceeded the cap. When
    /// <paramref name="skipCompleteFitCheck"/> is true, the caller already
    /// formatted the complete line and found it oversize. Do not serialize
    // / that complete payload again.
    /// encodes one message one time.
    /// </summary>
    public static IReadOnlyList<TerminalRenderCellsPayload> SliceToFit(
        TerminalRenderCellsPayload payload,
        string subscriptionId,
        int maxLineBytes = MaxNdjsonLineBytes,
        bool skipCompleteFitCheck = false)
    {
        var packed = SliceToFitPacked(payload, subscriptionId, maxLineBytes, skipCompleteFitCheck);
        if (packed.Count == 0)
            return [];
        var parts = new TerminalRenderCellsPayload[packed.Count];
        for (var i = 0; i < packed.Count; i++)
            parts[i] = packed[i].Payload;
        return parts;
    }

    /// <summary>
    /// Same as <see cref="SliceToFit"/>, and keep the one payload UTF-8 from
    /// the fit check so the emit path does not serialize the slice again.
    /// </summary>
    public static IReadOnlyList<AttachCellsPackedSlice> SliceToFitPacked(
        TerminalRenderCellsPayload payload,
        string subscriptionId,
        int maxLineBytes = MaxNdjsonLineBytes,
        bool skipCompleteFitCheck = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 256);

        if (!skipCompleteFitCheck
            && TryFormatFittingLine(payload, subscriptionId, maxLineBytes, out var completeUtf8, out _))
        {
            return [new AttachCellsPackedSlice(payload, completeUtf8)];
        }

        var rows = payload.Rows ?? [];
        if (rows.Count == 0)
            return [];

        var rowBytes = MeasureRowBytes(rows);
        var envelopeBytes = MeasureEnvelopeBytes(payload, subscriptionId);
        var parts = new List<AttachCellsPackedSlice>();
        var start = 0;
        while (start < rows.Count)
        {
            var best = start + 1;
            var lo = start + 1;
            var hi = rows.Count;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (RangeFits(envelopeBytes, rowBytes, start, mid, maxLineBytes))
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            var slice = WriteSlice(payload, rows, start, best, first: start == 0, last: best == rows.Count);
            if (!TryFormatFittingLine(slice, subscriptionId, maxLineBytes, out var utf8, out _))
                return [];
            parts.Add(new AttachCellsPackedSlice(slice, utf8));
            start = best;
        }

        return parts;
    }

    internal static bool LineFits(
        TerminalRenderCellsPayload payload,
        string subscriptionId,
        int maxLineBytes) =>
        TryFormatFittingLine(payload, subscriptionId, maxLineBytes, out _, out _);

    internal static bool TryFormatFittingLine(
        TerminalRenderCellsPayload payload,
        string subscriptionId,
        int maxLineBytes,
        out byte[] payloadUtf8,
        out byte[] line)
    {
        payloadUtf8 = VtCellPacker.SerializeUtf8(payload);
        line = FormatLineUtf8(payload, subscriptionId, payloadUtf8: payloadUtf8);
        return FitsNdjsonLine(line, maxLineBytes);
    }

    internal static string FormatLine(
        TerminalRenderCellsPayload payload,
        string subscriptionId,
        long seq = long.MaxValue,
        string? payloadJson = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var payloadUtf8 = payloadJson is null
            ? VtCellPacker.SerializeUtf8(payload)
            : Encoding.UTF8.GetBytes(payloadJson);
        return Encoding.UTF8.GetString(
            FormatLineUtf8(payload, subscriptionId, seq, payloadUtf8));
    }

    internal static byte[] FormatLineUtf8(
        TerminalRenderCellsPayload payload,
        string subscriptionId,
        long seq = long.MaxValue,
        byte[]? payloadUtf8 = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var utf8 = payloadUtf8 ?? VtCellPacker.SerializeUtf8(payload);
        var rec = new RuntimeEventRecord
        {
            Seq = seq,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = "",
            PayloadUtf8 = utf8,
            PayloadUtf8Trusted = true,
            Lane = IsOrderedCellsPayload(payload) ? WriterLaneNames.Ordered : WriterLaneNames.Render,
        };
        return EventSubscriptionHub.FormatRuntimeEventUtf8(rec, subscriptionId);
    }

    private static TerminalRenderCellsPayload WriteSlice(
        TerminalRenderCellsPayload source,
        IReadOnlyList<TerminalRenderCellRow> rows,
        int start,
        int end,
        bool first,
        bool last)
    {
        var sliceRows = new TerminalRenderCellRow[end - start];
        var changed = 0;
        for (var i = start; i < end; i++)
        {
            sliceRows[i - start] = rows[i];
            changed += rows[i].T?.Length ?? 0;
        }

        return source with
        {
            Full = source.Full && first,
            Reanchor = source.Reanchor && first,
            // Later slices are patches on the first slice. Attach applies a
            // cells patch only when BaseGeneration equals the last applied
            // generation (AttachSession.TryWriteCellsUnlocked).
            BaseGeneration = first ? source.BaseGeneration : source.Generation,
            Rows = sliceRows,
            Cursor = last ? source.Cursor : null,
            ChangedCells = changed,
            WireBytes = 0,
        };
    }

    private static int[] MeasureRowBytes(IReadOnlyList<TerminalRenderCellRow> rows)
    {
        var rowBytes = new int[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var json = JsonSerializer.Serialize(rows[i], ProtocolJsonContext.Default.TerminalRenderCellRow);
            rowBytes[i] = Encoding.UTF8.GetByteCount(json);
        }

        return rowBytes;
    }

    private static int MeasureEnvelopeBytes(TerminalRenderCellsPayload payload, string subscriptionId)
    {
        // Empty-row envelope only. Not the complete cells grid. The emit
        // path already serialized that grid once.
        var empty = payload with { Rows = Array.Empty<TerminalRenderCellRow>(), WireBytes = 0 };
        var emptyUtf8 = VtCellPacker.SerializeUtf8(empty);
        var line = FormatLineUtf8(empty, subscriptionId, payloadUtf8: emptyUtf8);
        return line.Length + 1 + AttachSnapshotPacker.ProbeSafetyBytes;
    }

    private static bool RangeFits(
        int envelopeBytes,
        int[] rowBytes,
        int start,
        int end,
        int maxLineBytes)
    {
        if (end <= start)
            return true;
        long total = envelopeBytes;
        for (var i = start; i < end && i < rowBytes.Length; i++)
        {
            total += rowBytes[i];
            if (i > start)
                total++;
        }

        return total <= maxLineBytes;
    }
}

/// <summary>
/// One cells slice plus the payload UTF-8 from the fit serialize.
/// </summary>
public readonly record struct AttachCellsPackedSlice(
    TerminalRenderCellsPayload Payload,
    byte[] PayloadUtf8);
