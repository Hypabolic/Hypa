using System.Buffers;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Packs a first-observe snapshot from the typed frame into live
/// <c>terminal.render</c> payload(s). Redacts cell text, then slices by
// / row when the NDJSON line would exceed the 1 MiB socket cap.
/// <c>src/protocol/wire.rs:1541-1558</c> <c>write_message</c> encodes the
/// typed message one time and measures the encoded bytes from the encoder
/// result. This packer writes each row one time with one
/// <c>Utf8JsonWriter</c> pass into a pooled buffer and reads the row byte
/// counts from the writer. It builds no text DOM.
/// </summary>
public static class AttachSnapshotPacker
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
    /// Redact cell text, then emit one or more snapshot payloads as UTF-8.
    /// The caller makes a string one time when it must.
    /// <paramref name="rowStart"/> is inclusive; <paramref name="rowEnd"/> is exclusive.
    /// </summary>
    public static IReadOnlyList<byte[]> Pack(
        string paneId,
        VtAttachSnapshot snapshot,
        IEventPayloadRedactor redactor,
        int maxLineBytes = MaxNdjsonLineBytes,
        long generation = 0,
        IReadOnlyList<int>? dirtyRows = null,
        int occupantGeneration = 0) =>
        Pack(paneId, snapshot, redactor, out _, maxLineBytes, generation, dirtyRows, occupantGeneration);

    /// <summary>
    /// Typed pack with a local materialization count. The counter fills
    /// once per posted part, not once per binary-search probe. Tests read
    /// the <c>out</c> value. Product callers use the public overload and
    /// ignore the count.
    /// </summary>
    internal static IReadOnlyList<byte[]> Pack(
        string paneId,
        VtAttachSnapshot snapshot,
        IEventPayloadRedactor redactor,
        out int writePartCount,
        int maxLineBytes = MaxNdjsonLineBytes,
        long generation = 0,
        IReadOnlyList<int>? dirtyRows = null,
        int occupantGeneration = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 256);
        writePartCount = 0;

        // into one message. Redact the typed cells in row-major order.
        var frame = AttachFrameRedactor.RedactCells(paneId, snapshot.Frame, redactor);
        var gridCols = frame.Cols;
        var gridRows = frame.Rows;
        if (gridCols < 1 || gridRows < 1)
            throw new InvalidOperationException("snapshot grid size is not positive");

        // One pooled scratch buffer holds every encoded row. Each row is
        // encoded one time with one Utf8JsonWriter pass; offsets are
        // recorded from the written count. The buffer returns to the pool
        // in finally. A single writer cannot emit consecutive root
        // values, so each row gets its own writer over the same buffer.
        using var scratch = new PooledBufferWriter(65536);
        var rowStarts = new int[gridRows];
        var rowLengths = new int[gridRows];
        for (var r = 0; r < gridRows; r++)
        {
            rowStarts[r] = scratch.WrittenCount;
            using (var rows = new Utf8JsonWriter(scratch))
            {
                WriteCellArray(rows, frame, r);
                rows.Flush();
            }

            rowLengths[r] = scratch.WrittenCount - rowStarts[r];
        }

        var scratchBytes = scratch;
        {
            var (envelopeBytes, lineOverhead) = MeasureEnvelope(
                paneId, snapshot, frame, gridCols, gridRows, generation, occupantGeneration);

            if (dirtyRows is { Count: > 0 })
            {
                var dirtyParts = PackDirtyRows(
                    paneId, snapshot, frame, gridCols, gridRows, generation, maxLineBytes, dirtyRows,
                    occupantGeneration, rowStarts, rowLengths, scratchBytes.WrittenSpan,
                    envelopeBytes, lineOverhead, ref writePartCount);
                EnsureClientBatchFits(dirtyParts);
                return dirtyParts;
            }

            var parts = new List<byte[]>();
            var start = 0;
            while (start < gridRows)
            {
                var best = start + 1;
                var lo = start + 1;
                var hi = gridRows;
                while (lo <= hi)
                {
                    var mid = lo + ((hi - lo) / 2);
                    if (RangeFits(envelopeBytes, rowLengths, start, mid, maxLineBytes))
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
                    paneId, start, best, best == gridRows, gridCols, gridRows, snapshot, frame,
                    start, best, generation, patch: false, occupantGeneration,
                    scratchBytes.WrittenSpan, rowStarts, rowLengths,
                    envelopeBytes, lineOverhead, maxLineBytes, ref writePartCount);
                parts.Add(part);
                start = best;
            }

            EnsureClientBatchFits(parts);
            return parts;
        }
    }

    private static IReadOnlyList<byte[]> PackDirtyRows(
        string paneId,
        VtAttachSnapshot snapshot,
        VtFrame frame,
        int gridCols,
        int gridRows,
        long generation,
        int maxLineBytes,
        IReadOnlyList<int> dirtyRows,
        int occupantGeneration,
        int[] rowStarts,
        int[] rowLengths,
        ReadOnlySpan<byte> scratch,
        int envelopeBytes,
        int lineOverhead,
        ref int writePartCount)
    {
        var ordered = dirtyRows
            .Where(r => r >= 0 && r < gridRows)
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

        var parts = new List<byte[]>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var complete = i == ranges.Count - 1;
            if (RangeFits(envelopeBytes, rowLengths, start, end, maxLineBytes))
            {
                var candidate = WritePart(
                    paneId, start, end, complete, gridCols, gridRows, snapshot, frame,
                    start, end, generation, patch: true, occupantGeneration,
                    scratch, rowStarts, rowLengths,
                    envelopeBytes, lineOverhead, maxLineBytes, ref writePartCount);
                parts.Add(candidate);
                continue;
            }

            for (var row = start; row < end; row++)
            {
                var rowComplete = complete && row == end - 1;
                var rowPart = WritePart(
                    paneId, row, row + 1, rowComplete, gridCols, gridRows, snapshot, frame,
                    row, row + 1, generation, patch: true, occupantGeneration,
                    scratch, rowStarts, rowLengths,
                    envelopeBytes, lineOverhead, maxLineBytes, ref writePartCount);
                parts.Add(rowPart);
            }
        }

        return parts;
    }

    internal static int EstimateClientBatchBytes(IReadOnlyList<byte[]> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var total = 0;
        foreach (var payload in payloads)
            total += 64 + Encoding.UTF8.GetCharCount(payload);
        return total;
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
        IReadOnlyList<byte[]> payloads,
        int maxBytes = MaxClientBatchBytes)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        return EstimateClientBatchBytes(payloads) <= maxBytes;
    }

    internal static bool FitsClientBatchCap(
        IReadOnlyList<string> payloads,
        int maxBytes = MaxClientBatchBytes)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        return EstimateClientBatchBytes(payloads) <= maxBytes;
    }

    private static void EnsureClientBatchFits(IReadOnlyList<byte[]> payloads)
    {
        if (!FitsClientBatchCap(payloads))
            throw new InvalidOperationException("snapshot batch exceeds client assembler cap");
    }

    /// <summary>
    /// The formatted NDJSON line for a 64-character subscription id. Used
    /// by tests to prove a part fits the socket cap.
    /// </summary>
    internal static bool LineFits(ReadOnlySpan<byte> payloadUtf8, int maxLineBytes)
    {
        var rec = new RuntimeEventRecord
        {
            Seq = long.MaxValue,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = "",
            PayloadUtf8 = payloadUtf8.ToArray(),
        };
        var line = EventSubscriptionHub.FormatRuntimeEventUtf8(
            rec, new string('s', EstimateSubscriptionIdChars));
        return line.Length + 1 <= maxLineBytes;
    }

    /// <summary>
    /// Exact fit check without formatting a line. The envelope overhead is
    /// constant for one probe subscription id, so the line length is the
    // / overhead plus the payload length.
    /// <c>src/protocol/wire.rs:1573-1578</c> refuses an oversized frame.
    /// </summary>
    private static bool LineFits(ReadOnlySpan<byte> payloadUtf8, int lineOverhead, int maxLineBytes) =>
        payloadUtf8.Length + lineOverhead + 1 <= maxLineBytes;

    /// <summary>
    /// Pooled <c>IBufferWriter&lt;byte&gt;</c> for one writer pass. Rents
    /// from <c>ArrayPool&lt;byte&gt;.Shared</c>, grows by doubling, and
    // / returns the buffer on dispose.
    /// <c>src/protocol/wire.rs:1541-1558</c> encodes one time and measures
    /// from the encoder result.
    /// </summary>
    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _written;
        private bool _disposed;

        public PooledBufferWriter(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(16, initialCapacity));
        }

        public int WrittenCount => _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (_disposed)
                throw new ObjectDisposedException(nameof(PooledBufferWriter));
            if (_written + count > _buffer.Length)
                throw new InvalidOperationException("buffer writer advanced past the end");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PooledBufferWriter));
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0)
                sizeHint = 256;
            if (_buffer.Length - _written >= sizeHint)
                return;
            var size = Math.Max(_buffer.Length * 2, _written + sizeHint);
            var bigger = ArrayPool<byte>.Shared.Rent(size);
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _written);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }

    /// <summary>
    /// Envelope estimate plus the constant NDJSON line overhead for the
    /// probe subscription id. The overhead is the formatted probe line
    /// minus the probe payload, so part fit checks need no second format.
    /// </summary>
    private static (int EnvelopeBytes, int LineOverhead) MeasureEnvelope(
        string paneId,
        VtAttachSnapshot snapshot,
        VtFrame frame,
        int gridCols,
        int gridRows,
        long generation,
        int occupantGeneration)
    {
        using var probe = new PooledBufferWriter(4096);
        using (var writer = new Utf8JsonWriter(probe))
        {
            WritePayloadEnvelope(
                writer, paneId, rowStart: 0, rowEnd: 0, complete: true,
                gridCols, gridRows, generation, patch: false, occupantGeneration);
            writer.WritePropertyName("snapshot"u8);
            WriteSnapshotScalars(writer, snapshot, frame, gridCols, partRows: 0);
            writer.WritePropertyName("cells"u8);
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            var payloadUtf8 = probe.WrittenSpan.ToArray();
            var rec = new RuntimeEventRecord
            {
                Seq = long.MaxValue,
                Class = EventClass.Render,
                Reliability = EventReliability.Render,
                Type = ProtocolEventTypes.TerminalRender,
                OccurredAt = DateTimeOffset.UnixEpoch,
                PayloadJson = "",
                PayloadUtf8 = payloadUtf8,
            };
            var line = EventSubscriptionHub.FormatRuntimeEventUtf8(
                rec, new string('s', EstimateSubscriptionIdChars));
            return (line.Length + 1 + ProbeSafetyBytes, line.Length - payloadUtf8.Length);
        }
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

    private static byte[] WritePart(
        string paneId,
        int rowStart,
        int rowEnd,
        bool complete,
        int gridCols,
        int gridRows,
        VtAttachSnapshot snapshot,
        VtFrame frame,
        int copyStart,
        int copyEnd,
        long generation,
        bool patch,
        int occupantGeneration,
        ReadOnlySpan<byte> scratch,
        int[] rowStarts,
        int[] rowLengths,
        int envelopeBytes,
        int lineOverhead,
        int maxLineBytes,
        ref int writePartCount)
    {
        writePartCount++;
        var partRows = Math.Max(0, copyEnd - copyStart);
        long rowTotal = 0;
        for (var r = copyStart; r < copyEnd; r++)
            rowTotal += rowLengths[r];
        using var buffer = new PooledBufferWriter((int)Math.Min(int.MaxValue, envelopeBytes + rowTotal + 64));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePayloadEnvelope(
                writer, paneId, rowStart, rowEnd, complete,
                gridCols, gridRows, generation, patch, occupantGeneration);
            writer.WritePropertyName("snapshot"u8);
            WriteSnapshotScalars(writer, snapshot, frame, gridCols, partRows);
            writer.WritePropertyName("cells"u8);
            writer.WriteStartArray();
            for (var r = copyStart; r < copyEnd; r++)
            {
                var slice = scratch.Slice(rowStarts[r], rowLengths[r]);
                writer.WriteRawValue(slice, skipInputValidation: true);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }

        var part = buffer.WrittenSpan.ToArray();
        // The line bound stays.
        if (!LineFits(part, lineOverhead, maxLineBytes))
            throw new InvalidOperationException("snapshot row exceeds NDJSON line cap");
        return part;
    }

    /// <summary>
    /// Payload object in declaration order. Source:
    /// <c>TerminalRenderSnapshotPayload</c> with
    /// <c>EventPayloadJsonContext</c> (no default ignore). Writes
    /// <c>pane_id</c> (omit when null), <c>target</c> (omit when null),
    /// <c>kind</c>, <c>row_start</c>, <c>row_end</c>, <c>complete</c>,
    /// <c>grid_cols</c>, <c>grid_rows</c>, <c>generation</c>,
    /// <c>patch</c> (omit when false), <c>occupant_generation</c> (omit
    /// when 0), <c>base_generation</c> (omit when 0), then
    /// <c>snapshot</c>.
    /// </summary>
    private static void WritePayloadEnvelope(
        Utf8JsonWriter writer,
        string paneId,
        int rowStart,
        int rowEnd,
        bool complete,
        int gridCols,
        int gridRows,
        long generation,
        bool patch,
        int occupantGeneration)
    {
        writer.WriteStartObject();
        if (string.Equals(paneId, ProtocolEventTypes.TerminalRenderTargetPopup, StringComparison.Ordinal))
            writer.WriteString("target"u8, ProtocolEventTypes.TerminalRenderTargetPopup);
        else
            writer.WriteString("pane_id"u8, paneId);
        writer.WriteString("kind"u8, "snapshot"u8);
        writer.WriteNumber("row_start"u8, rowStart);
        writer.WriteNumber("row_end"u8, rowEnd);
        writer.WriteBoolean("complete"u8, complete);
        writer.WriteNumber("grid_cols"u8, gridCols);
        writer.WriteNumber("grid_rows"u8, gridRows);
        writer.WriteNumber("generation"u8, generation);
        if (patch)
            writer.WriteBoolean("patch"u8, true);
        if (occupantGeneration != 0)
            writer.WriteNumber("occupant_generation"u8, occupantGeneration);
    }

    /// <summary>
    /// Snapshot object in declaration order. Source:
    /// <c>VtStructuredSnapshot</c> with <c>VtSnapshotWireJsonContext</c>
    /// (<c>WhenWritingDefault</c>). The old packer set <c>cols</c> and
    /// <c>rows</c> with the <c>JsonNode</c> indexer, which applies no
    /// ignore condition, so both keys are always written.
    /// </summary>
    private static void WriteSnapshotScalars(
        Utf8JsonWriter writer,
        VtAttachSnapshot snapshot,
        VtFrame frame,
        int gridCols,
        int partRows)
    {
        writer.WriteStartObject();
        if (snapshot.SchemaVersion != 0)
            writer.WriteNumber("schema_version"u8, snapshot.SchemaVersion);
        if (frame.Provider is not null)
            writer.WriteString("provider"u8, frame.Provider);
        writer.WriteNumber("cols"u8, gridCols);
        writer.WriteNumber("rows"u8, partRows);
        var cursor = frame.Cursor;
        if (cursor.HasCursor)
        {
            writer.WritePropertyName("cursor"u8);
            writer.WriteStartObject();
            if (cursor.Col != 0)
                writer.WriteNumber("col"u8, cursor.Col);
            if (cursor.Row != 0)
                writer.WriteNumber("row"u8, cursor.Row);
            writer.WriteBoolean("visible"u8, cursor.Visible);
            if (cursor.Shape != 0)
                writer.WriteNumber("shape"u8, cursor.Shape);
            writer.WriteEndObject();
        }

        writer.WriteString("active_screen"u8, frame.ActiveScreen);
        writer.WritePropertyName("scroll_region"u8);
        writer.WriteStartObject();
        if (snapshot.ScrollRegion.Top != 0)
            writer.WriteNumber("top"u8, snapshot.ScrollRegion.Top);
        if (snapshot.ScrollRegion.Bottom != 0)
            writer.WriteNumber("bottom"u8, snapshot.ScrollRegion.Bottom);
        writer.WriteEndObject();
        var modes = frame.Modes;
        writer.WritePropertyName("modes"u8);
        writer.WriteStartObject();
        if (modes.Origin)
            writer.WriteBoolean("origin"u8, true);
        if (modes.AutoWrap)
            writer.WriteBoolean("auto_wrap"u8, true);
        if (modes.Insert)
            writer.WriteBoolean("insert"u8, true);
        if (modes.BracketedPaste)
            writer.WriteBoolean("bracketed_paste"u8, true);
        if (modes.Mouse is not null)
            writer.WriteString("mouse"u8, modes.Mouse);
        if (modes.FocusReporting)
            writer.WriteBoolean("focus_reporting"u8, true);
        if (modes.SynchronizedOutput)
            writer.WriteBoolean("sync"u8, true);
        if (modes.ApplicationCursor)
            writer.WriteBoolean("application_cursor"u8, true);
        writer.WriteEndObject();
    }

    private static void WriteCellArray(Utf8JsonWriter writer, VtFrame frame, int row)
    {
        writer.WriteStartArray();
        for (var c = 0; c < frame.Cols; c++)
            WriteCell(writer, frame.CellAt(row, c));
        writer.WriteEndArray();
    }

    private static void WriteCell(Utf8JsonWriter writer, VtCellView cell)
    {
        writer.WriteStartObject();
        writer.WriteString("text"u8, cell.Text);
        if (cell.Width != 0)
            writer.WriteNumber("width"u8, cell.Width);
        if (cell.IsContinuation)
            writer.WriteBoolean("is_continuation"u8, true);
        writer.WritePropertyName("style"u8);
        writer.WriteStartObject();
        var fg = VtColorPack.ToWire(cell.FgPacked);
        if (fg is not null)
            writer.WriteString("fg"u8, fg);
        var bg = VtColorPack.ToWire(cell.BgPacked);
        if (bg is not null)
            writer.WriteString("bg"u8, bg);
        if (cell.Bold)
            writer.WriteBoolean("bold"u8, true);
        if (cell.Dim)
            writer.WriteBoolean("dim"u8, true);
        if (cell.Italic)
            writer.WriteBoolean("italic"u8, true);
        if (cell.Underline)
            writer.WriteBoolean("underline"u8, true);
        if (cell.Inverse)
            writer.WriteBoolean("inverse"u8, true);
        if (cell.Invisible)
            writer.WriteBoolean("invisible"u8, true);
        if (cell.Strikethrough)
            writer.WriteBoolean("strikethrough"u8, true);
        if (cell.Blink)
            writer.WriteBoolean("blink"u8, true);
        if (cell.Overline)
            writer.WriteBoolean("overline"u8, true);
        var underlineColor = VtColorPack.ToWire(cell.UnderlineColorPacked);
        if (underlineColor is not null)
            writer.WriteString("underline_color"u8, underlineColor);
        if (cell.UnderlineStyle != 0)
            writer.WriteNumber("underline_style"u8, cell.UnderlineStyle);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Parse a compact snapshot JSON string into a typed snapshot without
    /// a JSON DOM. The live snapshot fallback still captures JSON; this
    /// bridge moves that string onto the typed pack. Uses a streaming
    /// <c>Utf8JsonReader</c>: no <c>JsonNode.Parse</c>, no
    /// <c>JsonDocument.Parse</c>. Missing members fall back to the
    /// normalize defaults. Production JSON always carries every member.
    /// </summary>
    internal static VtAttachSnapshot ParseSnapshotJson(string paneId, string snapshotJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        var bytes = Encoding.UTF8.GetBytes(snapshotJson);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidOperationException("snapshot JSON is not an object");

        var schemaVersion = 0;
        string? provider = null;
        var cols = 1;
        var rows = 1;
        var hasCursor = false;
        var cursorCol = 0;
        var cursorRow = 0;
        var cursorVisible = false;
        var cursorShape = 0;
        string? activeScreen = null;
        var hasScrollRegion = false;
        var scrollTop = 0;
        var scrollBottom = 0;
        var hasModes = false;
        var modeOrigin = false;
        var modeAutoWrap = true;
        var modeInsert = false;
        var modeBracketedPaste = false;
        string? modeMouse = null;
        var modeFocusReporting = false;
        var modeSync = false;
        var modeApplicationCursor = false;
        var cellRows = new List<List<VtCellView>>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                SkipValue(ref reader);
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
                break;
            switch (name)
            {
                case "schema_version":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var sv))
                        schemaVersion = sv;
                    else
                        SkipValue(ref reader);
                    break;
                case "provider":
                    provider = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    SkipContainer(ref reader);
                    break;
                case "cols":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var c))
                        cols = Math.Max(1, c);
                    else
                        SkipValue(ref reader);
                    break;
                case "rows":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var r))
                        rows = Math.Max(1, r);
                    else
                        SkipValue(ref reader);
                    break;
                case "cursor":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        hasCursor = true;
                        ReadCursor(ref reader, ref cursorCol, ref cursorRow, ref cursorVisible, ref cursorShape);
                    }
                    else
                        SkipValue(ref reader);
                    break;
                case "active_screen":
                    activeScreen = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    SkipContainer(ref reader);
                    break;
                case "scroll_region":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        hasScrollRegion = true;
                        ReadScrollRegion(ref reader, ref scrollTop, ref scrollBottom);
                    }
                    else
                        SkipValue(ref reader);
                    break;
                case "modes":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        hasModes = true;
                        ReadModes(
                            ref reader, ref modeOrigin, ref modeAutoWrap, ref modeInsert,
                            ref modeBracketedPaste, ref modeMouse, ref modeFocusReporting, ref modeSync,
                            ref modeApplicationCursor);
                    }
                    else
                        SkipValue(ref reader);
                    break;
                case "cells":
                    if (reader.TokenType == JsonTokenType.StartArray)
                        ReadCells(ref reader, cellRows);
                    else
                        SkipValue(ref reader);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }

        rows = Math.Max(rows, cellRows.Count);
        for (var i = 0; i < cellRows.Count; i++)
            cols = Math.Max(cols, cellRows[i].Count);

        var tables = new VtStampTables();
        var packed = new VtFrameCell[cols * rows];
        for (var r = 0; r < rows; r++)
        {
            var row = r < cellRows.Count ? cellRows[r] : null;
            for (var c = 0; c < cols; c++)
            {
                var view = row is not null && c < row.Count ? row[c] : VtCellView.Blank;
                var style = new VtPackedStyle(
                    view.FgPacked, view.BgPacked, view.UnderlineColorPacked,
                    view.Modifier, view.UnderlineStylePacked);
                packed[(r * cols) + c] = tables.PackCell(
                    view.Text, Math.Max(1, view.Width), view.IsContinuation, in style, view.Hyperlink);
            }
        }

        var screen = activeScreen is "main" or "alt" ? activeScreen : "main";
        var resolvedProvider = string.IsNullOrEmpty(provider) ? "ghostty" : provider;
        var top = hasScrollRegion ? Math.Clamp(scrollTop, 0, rows - 1) : 0;
        var bottom = hasScrollRegion ? Math.Clamp(scrollBottom, top, rows - 1) : rows - 1;
        var frame = new VtFrame(
            paneId,
            cols,
            rows,
            new VtFrameGrid(packed, tables.Freeze(null), cols, rows),
            hasCursor
                ? new VtFrameCursor(cursorCol, cursorRow, cursorVisible, cursorShape, HasCursor: true)
                : VtFrameCursor.None,
            new VtFrameModes(
                modeOrigin, modeAutoWrap, modeInsert, modeBracketedPaste,
                hasModes && modeMouse is not null ? modeMouse : "none",
                modeFocusReporting, modeSync, MouseEncoding: null,
                ApplicationCursor: modeApplicationCursor),
            ViewportOrigin: 0,
            OccupantGeneration: 0,
            Generation: 0,
            Provider: resolvedProvider,
            ActiveScreen: screen);
        return new VtAttachSnapshot(frame, new VtScrollRegion(top, bottom), schemaVersion);
    }

    private static void ReadCursor(
        ref Utf8JsonReader reader, ref int col, ref int row, ref bool visible, ref int shape)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                SkipValue(ref reader);
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
                return;
            switch (name)
            {
                case "col":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var c))
                        col = c;
                    else
                        SkipValue(ref reader);
                    break;
                case "row":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var r))
                        row = r;
                    else
                        SkipValue(ref reader);
                    break;
                case "visible":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        visible = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "shape":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var s))
                        shape = s;
                    else
                        SkipValue(ref reader);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }
    }

    private static void ReadScrollRegion(ref Utf8JsonReader reader, ref int top, ref int bottom)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                SkipValue(ref reader);
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
                return;
            if (string.Equals(name, "top", StringComparison.Ordinal)
                && reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var t))
            {
                top = t;
            }
            else if (string.Equals(name, "bottom", StringComparison.Ordinal)
                && reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var b))
            {
                bottom = b;
            }
            else
            {
                SkipValue(ref reader);
            }
        }
    }

    private static void ReadModes(
        ref Utf8JsonReader reader, ref bool origin, ref bool autoWrap, ref bool insert,
        ref bool bracketedPaste, ref string? mouse, ref bool focusReporting, ref bool sync,
        ref bool applicationCursor)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                SkipValue(ref reader);
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
                return;
            switch (name)
            {
                case "origin":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        origin = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "auto_wrap":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        autoWrap = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "insert":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        insert = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "bracketed_paste":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        bracketedPaste = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "mouse":
                    mouse = reader.TokenType == JsonTokenType.String ? reader.GetString() : mouse;
                    SkipContainer(ref reader);
                    break;
                case "focus_reporting":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        focusReporting = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "sync":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        sync = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "application_cursor":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        applicationCursor = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }
    }

    private static void ReadCells(ref Utf8JsonReader reader, List<List<VtCellView>> cellRows)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return;
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                SkipValue(ref reader);
                continue;
            }

            var row = new List<VtCellView>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    SkipValue(ref reader);
                    continue;
                }

                row.Add(ReadCell(ref reader));
            }

            cellRows.Add(row);
        }
    }

    private static VtCellView ReadCell(ref Utf8JsonReader reader)
    {
        var text = " ";
        var width = 1;
        var isContinuation = false;
        string? fg = null;
        string? bg = null;
        var bold = false;
        var dim = false;
        var italic = false;
        var underline = false;
        var inverse = false;
        var invisible = false;
        var strikethrough = false;
        var blink = false;
        var overline = false;
        string? underlineColor = null;
        var underlineStyle = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                SkipValue(ref reader);
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
                break;
            switch (name)
            {
                case "text":
                    if (reader.TokenType == JsonTokenType.String)
                        text = reader.GetString() ?? " ";
                    else
                        SkipValue(ref reader);
                    break;
                case "width":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var w))
                        width = w;
                    else
                        SkipValue(ref reader);
                    break;
                case "is_continuation":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        isContinuation = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "style":
                    if (reader.TokenType == JsonTokenType.StartObject)
                        ReadStyle(
                            ref reader, ref fg, ref bg, ref bold, ref dim, ref italic,
                            ref underline, ref inverse, ref invisible, ref strikethrough,
                            ref blink, ref overline, ref underlineColor, ref underlineStyle);
                    else
                        SkipValue(ref reader);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }

        return new VtCellView(
            text, width, isContinuation, fg, bg, bold, dim, italic, underline,
            inverse, invisible, strikethrough, blink, overline, underlineColor, underlineStyle);
    }

    private static void ReadStyle(
        ref Utf8JsonReader reader, ref string? fg, ref string? bg, ref bool bold, ref bool dim,
        ref bool italic, ref bool underline, ref bool inverse, ref bool invisible,
        ref bool strikethrough, ref bool blink, ref bool overline,
        ref string? underlineColor, ref int underlineStyle)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                SkipValue(ref reader);
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
                return;
            switch (name)
            {
                case "fg":
                    fg = reader.TokenType == JsonTokenType.String ? reader.GetString() : fg;
                    SkipContainer(ref reader);
                    break;
                case "bg":
                    bg = reader.TokenType == JsonTokenType.String ? reader.GetString() : bg;
                    SkipContainer(ref reader);
                    break;
                case "bold":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        bold = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "dim":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        dim = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "italic":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        italic = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "underline":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        underline = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "inverse":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        inverse = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "invisible":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        invisible = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "strikethrough":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        strikethrough = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "blink":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        blink = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "overline":
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                        overline = reader.GetBoolean();
                    else
                        SkipValue(ref reader);
                    break;
                case "underline_color":
                    underlineColor = reader.TokenType == JsonTokenType.String ? reader.GetString() : underlineColor;
                    SkipContainer(ref reader);
                    break;
                case "underline_style":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var s))
                        underlineStyle = s;
                    else
                        SkipValue(ref reader);
                    break;
                default:
                    SkipValue(ref reader);
                    break;
            }
        }
    }

    private static void SkipContainer(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
    }

    /// <summary>
    /// Distinct from the live PTY stream key so snapshot pack does not flush
    /// in-flight Output carry for <paramref name="paneId"/>.
    /// </summary>
    internal static string SnapshotStreamKey(string paneId) => paneId + "\u001fattach-snapshot";
}
