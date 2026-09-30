using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// Frozen byte oracle for the pre-typed snapshot packer. Test code only.
/// Mirrors the <c>JsonNode</c> pack algorithm so parity tests compare the
/// new typed pack against these bytes. Never used by product code.
/// </summary>
public static class LegacySnapshotPackReference
{
    /// <summary>Matches <c>UnixSocketServerOptions.DefaultMaxLineBytes</c>.</summary>
    public const int MaxNdjsonLineBytes = 1_048_576;

    /// <summary>
    /// Matches the attach client assembler buffer. A complete batch that
    /// cannot fit this cap must not queue; the client would reset and drop it.
    /// </summary>
    public const int MaxClientBatchBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Conservative subscription id used when estimating envelope size.
    /// Real ids are shorter.
    /// </summary>
    internal const int EstimateSubscriptionIdChars = 64;

    /// <summary>
    /// Extra bytes on top of the empty-cells envelope so a probe never
    /// under-counts the NDJSON line.
    /// </summary>
    internal const int ProbeSafetyBytes = 256;

    /// <summary>
    /// Count of <see cref="WritePart"/> materializations. Tests reset this
    /// to prove probes do not clone the full grid.
    /// </summary>
    internal static int WritePartInvocations;

    /// <summary>
    /// Redact cell text, then emit one or more compact snapshot payloads.
    /// <paramref name="rowStart"/> is inclusive; <paramref name="rowEnd"/> is exclusive.
    /// </summary>
    public static IReadOnlyList<string> Pack(
        string paneId,
        string snapshotJson,
        IEventPayloadRedactor redactor,
        int maxLineBytes = MaxNdjsonLineBytes,
        long generation = 0,
        IReadOnlyList<int>? dirtyRows = null,
        int occupantGeneration = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 256);

        var root = JsonNode.Parse(snapshotJson)?.AsObject()
            ?? throw new InvalidOperationException("snapshot JSON is not an object");
        RedactCells(paneId, root, redactor);

        var cells = root["cells"] as JsonArray ?? [];
        var gridRows = ReadPositiveInt(root["rows"], fallback: Math.Max(1, cells.Count));
        var gridCols = ReadPositiveInt(root["cols"], fallback: 1);
        var rowCount = cells.Count;
        var rowBytes = MeasureRowBytes(cells);
        var envelopeBytes = MeasureEnvelopeBytes(
            paneId, root, gridCols, gridRows, generation, occupantGeneration, patch: false);

        if (rowCount == 0)
        {
            if (gridRows > 0)
                throw new InvalidOperationException("empty snapshot cells with positive grid");
            return
            [
                WritePart(
                    paneId, 0, 0, complete: true, gridCols, gridRows, root, cells, 0, 0,
                    generation, patch: false, occupantGeneration),
            ];
        }

        if (dirtyRows is { Count: > 0 })
        {
            var dirtyParts = PackDirtyRows(
                paneId, root, cells, gridCols, gridRows, generation, maxLineBytes, dirtyRows,
                occupantGeneration, rowBytes, envelopeBytes);
            EnsureClientBatchFits(dirtyParts);
            return dirtyParts;
        }

        var parts = new List<string>();
        var start = 0;
        while (start < rowCount)
        {
            var best = start + 1;
            var lo = start + 1;
            var hi = rowCount;
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

            var part = WritePart(
                paneId, start, best, best == rowCount, gridCols, gridRows, root, cells, start, best,
                generation, patch: false, occupantGeneration);
            if (!LineFits(part, maxLineBytes))
                throw new InvalidOperationException("snapshot row exceeds NDJSON line cap");
            parts.Add(part);
            start = best;
        }

        EnsureClientBatchFits(parts);
        return parts;
    }

    private static IReadOnlyList<string> PackDirtyRows(
        string paneId,
        JsonObject root,
        JsonArray cells,
        int gridCols,
        int gridRows,
        long generation,
        int maxLineBytes,
        IReadOnlyList<int> dirtyRows,
        int occupantGeneration,
        int[] rowBytes,
        int envelopeBytes)
    {
        var ordered = dirtyRows
            .Where(r => r >= 0 && r < cells.Count && r < gridRows)
            .Distinct()
            .OrderBy(r => r)
            .ToArray();
        if (ordered.Length == 0)
            throw new InvalidOperationException("dirty paint has no in-range rows");

        var ranges = new List<(int Start, int End)>();
        var rangeStart = ordered[0];
        var rangeEnd = ordered[0] + 1;
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i] == rangeEnd)
            {
                rangeEnd++;
                continue;
            }

            ranges.Add((rangeStart, rangeEnd));
            rangeStart = ordered[i];
            rangeEnd = ordered[i] + 1;
        }

        ranges.Add((rangeStart, rangeEnd));

        var dirtyEnvelope = envelopeBytes > 0
            ? envelopeBytes
            : MeasureEnvelopeBytes(
                paneId, root, gridCols, gridRows, generation, occupantGeneration, patch: true);
        var parts = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var complete = i == ranges.Count - 1;
            if (RangeFits(dirtyEnvelope, rowBytes, start, end, maxLineBytes))
            {
                var candidate = WritePart(
                    paneId, start, end, complete, gridCols, gridRows, root, cells, start, end,
                    generation, patch: true, occupantGeneration);
                if (!LineFits(candidate, maxLineBytes))
                    throw new InvalidOperationException("snapshot row exceeds NDJSON line cap");
                parts.Add(candidate);
                continue;
            }

            for (var row = start; row < end; row++)
            {
                var rowComplete = complete && row == end - 1;
                var rowPart = WritePart(
                    paneId, row, row + 1, rowComplete, gridCols, gridRows, root, cells, row, row + 1,
                    generation, patch: true, occupantGeneration);
                if (!LineFits(rowPart, maxLineBytes))
                    throw new InvalidOperationException("snapshot row exceeds NDJSON line cap");
                parts.Add(rowPart);
            }
        }

        return parts;
    }

    /// <summary>
    /// Grid size from a compact VT snapshot object. Used to force a full
    /// paint when the client has no retained frame of this size.
    /// </summary>
    internal static bool TryReadGridSize(string snapshotJson, out int cols, out int rows)
    {
        cols = 0;
        rows = 0;
        if (string.IsNullOrWhiteSpace(snapshotJson))
            return false;

        try
        {
            var root = JsonNode.Parse(snapshotJson)?.AsObject();
            if (root is null)
                return false;
            var cells = root["cells"] as JsonArray ?? [];
            rows = ReadPositiveInt(root["rows"], fallback: Math.Max(1, cells.Count));
            cols = ReadPositiveInt(root["cols"], fallback: 1);
            return cols > 0 && rows > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static int EstimateClientBatchBytes(IReadOnlyList<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var total = 0;
        foreach (var payload in payloads)
            total += 64 + payload.Length;
        return total;
    }

    internal static bool FitsClientBatchCap(
        IReadOnlyList<string> payloads,
        int maxBytes = MaxClientBatchBytes)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        return EstimateClientBatchBytes(payloads) <= maxBytes;
    }

    private static void EnsureClientBatchFits(IReadOnlyList<string> payloads)
    {
        if (!FitsClientBatchCap(payloads))
            throw new InvalidOperationException("snapshot batch exceeds client assembler cap");
    }

    internal static bool LineFits(string payloadJson, int maxLineBytes)
    {
        var rec = new RuntimeEventRecord
        {
            Seq = long.MaxValue,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payloadJson,
        };
        var line = EventSubscriptionHub.FormatRuntimeEventLine(
            rec, new string('s', EstimateSubscriptionIdChars));
        return line.Length + 1 <= maxLineBytes;
    }

    private static int[] MeasureRowBytes(JsonArray cells)
    {
        var rowBytes = new int[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            var json = cells[i] is JsonArray row
                ? row.ToJsonString()
                : "[]";
            rowBytes[i] = Encoding.UTF8.GetByteCount(json);
        }

        return rowBytes;
    }

    private static int MeasureEnvelopeBytes(
        string paneId,
        JsonObject root,
        int gridCols,
        int gridRows,
        long generation,
        int occupantGeneration,
        bool patch)
    {
        var empty = root.DeepClone()!.AsObject();
        empty["cells"] = new JsonArray();
        empty["rows"] = 0;
        empty["cols"] = gridCols;
        using var snapDoc = JsonDocument.Parse(empty.ToJsonString());
        var payload = string.Equals(paneId, ProtocolEventTypes.TerminalRenderTargetPopup, StringComparison.Ordinal)
            ? RuntimeEventPayloadJson.WritePopupTerminalRenderSnapshot(
                0, 0, complete: true, gridCols, gridRows, snapDoc.RootElement.Clone(),
                generation, patch, occupantGeneration)
            : RuntimeEventPayloadJson.WriteTerminalRenderSnapshot(
                paneId, 0, 0, complete: true, gridCols, gridRows, snapDoc.RootElement.Clone(),
                generation, patch, occupantGeneration);
        var rec = new RuntimeEventRecord
        {
            Seq = long.MaxValue,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payload,
        };
        var line = EventSubscriptionHub.FormatRuntimeEventLine(
            rec, new string('s', EstimateSubscriptionIdChars));
        return line.Length + 1 + ProbeSafetyBytes;
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

    private static string WritePart(
        string paneId,
        int rowStart,
        int rowEnd,
        bool complete,
        int gridCols,
        int gridRows,
        JsonObject root,
        JsonArray cells,
        int copyStart,
        int copyEnd,
        long generation,
        bool patch,
        int occupantGeneration)
    {
        Interlocked.Increment(ref WritePartInvocations);
        var slice = root.DeepClone()!.AsObject();
        var partCells = new JsonArray();
        for (var r = copyStart; r < copyEnd && r < cells.Count; r++)
            partCells.Add(cells[r]!.DeepClone());
        slice["cells"] = partCells;
        slice["rows"] = Math.Max(0, copyEnd - copyStart);
        slice["cols"] = gridCols;

        using var snapDoc = JsonDocument.Parse(slice.ToJsonString());
        if (string.Equals(paneId, ProtocolEventTypes.TerminalRenderTargetPopup, StringComparison.Ordinal))
        {
            return RuntimeEventPayloadJson.WritePopupTerminalRenderSnapshot(
                rowStart,
                rowEnd,
                complete,
                gridCols,
                gridRows,
                snapDoc.RootElement.Clone(),
                generation,
                patch,
                occupantGeneration);
        }

        return RuntimeEventPayloadJson.WriteTerminalRenderSnapshot(
            paneId,
            rowStart,
            rowEnd,
            complete,
            gridCols,
            gridRows,
            snapDoc.RootElement.Clone(),
            generation,
            patch,
            occupantGeneration);
    }

    /// <summary>
    /// Distinct from the live PTY stream key so snapshot pack does not flush
    /// in-flight Output carry for <paramref name="paneId"/>.
    /// </summary>
    internal static string SnapshotStreamKey(string paneId) => paneId + "\u001fattach-snapshot";

    /// <summary>
    /// Join all non-continuation cell text in row-major order (no inserted
    /// newlines). Run the same stream-keyed redactor as live bytes, then flush
    /// so wrap-around PEM and tokens match. When redaction is a no-op, leave
    /// cell text unchanged so Ghostty graphemes stay intact. Otherwise write
    /// back by each cell's original <c>text</c> length.
    /// </summary>
    private static void RedactCells(string paneId, JsonObject root, IEventPayloadRedactor redactor)
    {
        if (root["cells"] is not JsonArray rows)
            return;

        var joined = new StringBuilder();
        var active = new List<JsonObject>();
        foreach (var rowNode in rows)
        {
            if (rowNode is not JsonArray row)
                continue;

            foreach (var cellNode in row)
            {
                if (cellNode is not JsonObject cell)
                    continue;
                if (cell["is_continuation"] is JsonValue cont
                    && cont.TryGetValue<bool>(out var isCont)
                    && isCont)
                {
                    continue;
                }

                joined.Append(cell["text"]?.GetValue<string>() ?? " ");
                active.Add(cell);
            }
        }

        if (active.Count == 0)
            return;

        var raw = joined.ToString();
        var streamKey = SnapshotStreamKey(paneId);
        var emitted = redactor.RedactTerminalBytes(streamKey, Encoding.UTF8.GetBytes(raw));
        var flushed = redactor.FlushTerminalStream(streamKey);
        var redacted = Encoding.UTF8.GetString(ConcatUtf8(emitted, flushed).Span);

        if (redacted == raw)
            return;

        var offset = 0;
        foreach (var cell in active)
        {
            var original = cell["text"]?.GetValue<string>() ?? " ";
            var n = original.Length;
            if (n < 1)
                n = 1;
            if (offset >= redacted.Length)
            {
                cell["text"] = " ";
                continue;
            }

            var take = Math.Min(n, redacted.Length - offset);
            cell["text"] = redacted.Substring(offset, take);
            offset += take;
        }
    }

    private static ReadOnlyMemory<byte> ConcatUtf8(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        if (first.IsEmpty)
            return second;
        if (second.IsEmpty)
            return first;

        var buf = new byte[first.Length + second.Length];
        first.Span.CopyTo(buf);
        second.Span.CopyTo(buf.AsSpan(first.Length));
        return buf;
    }

    private static int ReadPositiveInt(JsonNode? node, int fallback)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var n) && n > 0)
            return n;
        return fallback < 1 ? 1 : fallback;
    }
}
