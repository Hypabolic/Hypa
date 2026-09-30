using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.Cli.Attach;

/// <summary>
/// Buffers <c>kind=snapshot</c> slices. One batch per pane and generation.
/// Paints only when <c>complete=true</c>. Interleaved sibling and generation
/// snapshots assemble independently.
/// </summary>
public sealed class SnapshotAssembler
{
    public const int MaxBatches = 16;
    public const int MaxBufferedBytes = 2 * 1024 * 1024;

    private readonly Dictionary<BatchKey, Batch> _batches = [];
    private readonly Dictionary<string, AssembledSnapshot> _retained = new(StringComparer.Ordinal);

    public bool HasCompleteFrame { get; private set; }

    public AssembledSnapshot? LastComplete { get; private set; }

    public bool ReanchorRequired { get; private set; }

    public bool ConsumeReanchorRequest()
    {
        var required = ReanchorRequired;
        ReanchorRequired = false;
        return required;
    }

    public int BufferedSliceCount
    {
        get
        {
            var count = 0;
            foreach (var batch in _batches.Values)
                count += batch.Slices.Count;
            return count;
        }
    }

    public bool TryAdd(TerminalRenderSnapshotPayload slice, out AssembledSnapshot? complete)
    {
        ArgumentNullException.ThrowIfNull(slice);
        complete = null;

        if (!string.Equals(slice.Kind, TerminalRenderSnapshotPayload.KindSnapshot, StringComparison.Ordinal))
            return false;
        var paneId = slice.PaneId;
        if (string.Equals(slice.Target, ProtocolEventTypes.TerminalRenderTargetPopup, StringComparison.Ordinal))
            paneId = ProtocolEventTypes.TerminalRenderTargetPopup;
        if (string.IsNullOrWhiteSpace(paneId))
            return false;

        var key = new BatchKey(paneId, slice.Generation);
        if (!_batches.TryGetValue(key, out var batch))
        {
            if (_batches.Count >= MaxBatches)
                EvictOldestIncomplete();
            if (_batches.Count >= MaxBatches)
                return false;

            batch = new Batch(slice.Generation);
            _batches[key] = batch;
        }

        var addedBytes = EstimateBytes(slice);
        if (BufferedBytes() + addedBytes > MaxBufferedBytes)
        {
            batch.Reset(slice.Generation);
            if (BufferedBytes() + addedBytes > MaxBufferedBytes)
            {
                _batches.Remove(key);
                return false;
            }
        }

        batch.Slices.Add(slice);
        batch.Bytes += addedBytes;

        if (!slice.Complete)
            return false;

        var slices = batch.Slices;
        _batches.Remove(key);
        if (slice.GridRows <= 0 || slice.GridCols <= 0)
            return false;

        AssembledSnapshot? assembled = null;
        if (slice.Patch)
        {
            if (!_retained.TryGetValue(paneId, out var retained)
                || retained.Rows != slice.GridRows
                || retained.Cols != slice.GridCols
                || !SameOccupant(retained, slice.OccupantGeneration)
                || BaseGenerationMismatch(retained, slice))
            {
                ReanchorRequired = true;
                return false;
            }

            if (!TryApplyPatch(retained, slices, out assembled) || assembled is null)
            {
                ReanchorRequired = true;
                return false;
            }
        }
        else if (!TryAssemble(slices, out assembled) || assembled is null)
        {
            if (!_retained.TryGetValue(paneId, out var retained)
                || retained.Rows != slice.GridRows
                || retained.Cols != slice.GridCols
                || !SameOccupant(retained, slice.OccupantGeneration))
            {
                return false;
            }

            if (!TryFillFromRetained(retained, slices, out assembled) || assembled is null)
                return false;
        }

        DropOlderIncomplete(paneId, slice.Generation);

        if (assembled is not null && assembled.OccupantGeneration <= 0 && slice.OccupantGeneration > 0)
            assembled = assembled with { OccupantGeneration = slice.OccupantGeneration };

        LastComplete = assembled;
        HasCompleteFrame = true;
        _retained[paneId] = assembled!;
        complete = assembled;
        return true;
    }

    public void Reset()
    {
        _batches.Clear();
        _retained.Clear();
        HasCompleteFrame = false;
        LastComplete = null;
        ReanchorRequired = false;
    }

    /// <summary>
    /// Drop the retained frame and incomplete batches for <paramref name="paneId"/>.
    /// Occupant swap must call this so a torn attach Full cannot mix the previous grid.
    /// </summary>
    public void DropRetained(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;

        _retained.Remove(paneId);
        List<BatchKey>? stale = null;
        foreach (var key in _batches.Keys)
        {
            if (string.Equals(key.PaneId, paneId, StringComparison.Ordinal))
                (stale ??= []).Add(key);
        }

        if (stale is not null)
        {
            foreach (var key in stale)
                _batches.Remove(key);
        }

        if (LastComplete is { } last && string.Equals(last.PaneId, paneId, StringComparison.Ordinal))
        {
            LastComplete = null;
            HasCompleteFrame = false;
        }
    }

    private int BufferedBytes()
    {
        var bytes = 0;
        foreach (var batch in _batches.Values)
            bytes += batch.Bytes;
        return bytes;
    }

    private void DropOlderIncomplete(string paneId, long generation)
    {
        List<BatchKey>? stale = null;
        foreach (var (key, _) in _batches)
        {
            if (key.PaneId == paneId && key.Generation < generation)
                (stale ??= []).Add(key);
        }

        if (stale is null)
            return;
        foreach (var key in stale)
            _batches.Remove(key);
    }

    private void EvictOldestIncomplete()
    {
        BatchKey? victim = null;
        foreach (var (key, batch) in _batches)
        {
            if (batch.Slices.Count == 0)
            {
                victim = key;
                break;
            }

            victim ??= key;
        }

        if (victim is { } evict)
        {
            _batches.Remove(evict);
            ReanchorRequired = true;
        }
    }

    private readonly record struct BatchKey(string PaneId, long Generation);

    private static int EstimateBytes(TerminalRenderSnapshotPayload slice)
    {
        var raw = slice.Snapshot.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? 0
            : slice.Snapshot.GetRawText().Length;
        return 64 + (slice.PaneId?.Length ?? 0) + raw;
    }

    private sealed class Batch(long generation)
    {
        public long Generation { get; private set; } = generation;

        public List<TerminalRenderSnapshotPayload> Slices { get; } = [];

        public int Bytes { get; set; }

        public void Reset(long generation)
        {
            Generation = generation;
            Slices.Clear();
            Bytes = 0;
        }
    }

    private static bool TryAssemble(
        IReadOnlyList<TerminalRenderSnapshotPayload> slices,
        out AssembledSnapshot? assembled)
    {
        assembled = null;
        if (slices.Count == 0)
            return false;

        var last = slices[^1];
        var rows = last.GridRows > 0 ? last.GridRows : 0;
        var cols = last.GridCols > 0 ? last.GridCols : 0;
        if (rows <= 0 || cols <= 0)
            return false;

        var generation = slices[0].Generation;
        var covered = new bool[rows];
        JsonElement? firstSnap = null;
        foreach (var slice in slices)
        {
            if (slice.Generation != generation)
                return false;
            if (slice.RowStart < 0 || slice.RowEnd < slice.RowStart)
                return false;
            if (slice.RowEnd > rows)
                return false;
            for (var r = slice.RowStart; r < slice.RowEnd && r < rows; r++)
                covered[r] = true;
            if (slice.Snapshot.ValueKind == JsonValueKind.Object && firstSnap is null)
                firstSnap = slice.Snapshot;
        }

        for (var i = 0; i < covered.Length; i++)
        {
            if (!covered[i])
                return false;
        }

        var provider = "ghostty";
        var activeScreen = "main";
        if (firstSnap is { } snap && snap.ValueKind == JsonValueKind.Object)
        {
            if (snap.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String)
                provider = p.GetString() ?? provider;
            if (snap.TryGetProperty("active_screen", out var scr) && scr.ValueKind == JsonValueKind.String)
                activeScreen = scr.GetString() ?? activeScreen;
        }

        var cells = MergeCells(slices, rows, cols);
        if (cells.Count == 0 && rows > 0)
            return false;
        var assembledSnap = firstSnap ?? last.Snapshot;
        assembled = new AssembledSnapshot(
            last.PaneId
                ?? (string.Equals(last.Target, ProtocolEventTypes.TerminalRenderTargetPopup, StringComparison.Ordinal)
                    ? ProtocolEventTypes.TerminalRenderTargetPopup
                    : ""),
            cols,
            rows,
            provider,
            activeScreen,
            cells,
            assembledSnap,
            ReadCursor(assembledSnap),
            last.OccupantGeneration,
            generation,
            ReadSync(assembledSnap),
            IngestFull: true,
            BracketedPaste: ReadModeFlag(assembledSnap, "bracketed_paste"),
            ApplicationCursor: ReadModeFlag(assembledSnap, "application_cursor"));
        return true;
    }

    private static bool SameOccupant(AssembledSnapshot retained, int incoming)
    {
        if (incoming <= 0)
            return true;
        return retained.OccupantGeneration == incoming;
    }

    private static bool BaseGenerationMismatch(AssembledSnapshot retained, TerminalRenderSnapshotPayload slice)
    {
        if (slice.BaseGeneration <= 0 || retained.Generation <= 0)
            return false;
        return slice.BaseGeneration != retained.Generation;
    }

    internal static AssembledCursor ReadCursor(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("cursor", out var cur)
            || cur.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return AssembledCursor.Default;
        }

        if (cur.ValueKind != JsonValueKind.Object)
            return AssembledCursor.Default;

        if (cur.TryGetProperty("has_cursor", out var has)
            && has.ValueKind == JsonValueKind.False)
        {
            return AssembledCursor.Default;
        }

        var col = 0;
        var row = 0;
        // Compact wire omits false. Missing visible must not mean shown.
        var visible = false;
        if (cur.TryGetProperty("col", out var c) && c.TryGetInt32(out var ci) && ci >= 0)
            col = ci;
        if (cur.TryGetProperty("row", out var r) && r.TryGetInt32(out var ri) && ri >= 0)
            row = ri;
        if (cur.TryGetProperty("visible", out var v))
            visible = v.ValueKind == JsonValueKind.True;
        var shape = 0;
        if (cur.TryGetProperty("shape", out var sh) && sh.TryGetInt32(out var si))
            shape = Math.Clamp(si, 0, 6);
        return new AssembledCursor(col, row, visible, shape, HasCursor: true);
    }

    internal static bool ReadModeFlag(JsonElement snap, string name)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return false;
        if (!snap.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Object)
            return false;
        if (!modes.TryGetProperty(name, out var flag))
            return false;
        return flag.ValueKind == JsonValueKind.True;
    }

    internal static bool ReadSync(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return false;
        if (!snap.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Object)
            return false;
        if (!modes.TryGetProperty("sync", out var sync))
            return false;
        return sync.ValueKind == JsonValueKind.True;
    }

    private static IReadOnlyList<IReadOnlyList<AssembledCell>> MergeCells(
        IReadOnlyList<TerminalRenderSnapshotPayload> slices,
        int rows,
        int cols)
    {
        var grid = new AssembledCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            grid[r] = new AssembledCell[cols];
            for (var c = 0; c < cols; c++)
                grid[r][c] = AssembledCell.Blank;
        }

        foreach (var slice in slices)
        {
            if (slice.Snapshot.ValueKind != JsonValueKind.Object)
                continue;
            if (!slice.Snapshot.TryGetProperty("cells", out var cellsEl)
                || cellsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var localRow = 0;
            foreach (var rowEl in cellsEl.EnumerateArray())
            {
                var destRow = slice.RowStart + localRow;
                localRow++;
                if (destRow < 0 || destRow >= rows || rowEl.ValueKind != JsonValueKind.Array)
                    continue;

                var col = 0;
                foreach (var cellEl in rowEl.EnumerateArray())
                {
                    if (col >= cols)
                        break;
                    grid[destRow][col] = AssembledCell.FromJson(cellEl);
                    col++;
                }
            }
        }

        return grid;
    }

    private static bool TryApplyPatch(
        AssembledSnapshot retained,
        IReadOnlyList<TerminalRenderSnapshotPayload> slices,
        out AssembledSnapshot? assembled)
    {
        assembled = null;
        if (slices.Count == 0)
            return false;

        var rows = retained.Rows;
        var cols = retained.Cols;
        var grid = CloneCells(retained.Cells, rows, cols);
        foreach (var slice in slices)
        {
            if (slice.RowStart < 0 || slice.RowEnd < slice.RowStart || slice.RowEnd > rows)
                return false;
            CopySliceRows(grid, slice, rows, cols);
        }

        var last = slices[^1];
        assembled = retained with
        {
            Cells = grid,
            Snapshot = last.Snapshot.ValueKind == JsonValueKind.Object ? last.Snapshot : retained.Snapshot,
            Cursor = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadCursor(last.Snapshot)
                : retained.Cursor,
            OccupantGeneration = last.OccupantGeneration > 0
                ? last.OccupantGeneration
                : retained.OccupantGeneration,
            Generation = last.Generation > 0 ? last.Generation : retained.Generation,
            SynchronizedOutput = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadSync(last.Snapshot)
                : retained.SynchronizedOutput,
            BracketedPaste = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadModeFlag(last.Snapshot, "bracketed_paste")
                : retained.BracketedPaste,
            ApplicationCursor = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadModeFlag(last.Snapshot, "application_cursor")
                : retained.ApplicationCursor,
        };
        return true;
    }

    private static bool TryFillFromRetained(
        AssembledSnapshot retained,
        IReadOnlyList<TerminalRenderSnapshotPayload> slices,
        out AssembledSnapshot? assembled)
    {
        assembled = null;
        if (slices.Count == 0)
            return false;

        var last = slices[^1];
        var rows = last.GridRows > 0 ? last.GridRows : 0;
        var cols = last.GridCols > 0 ? last.GridCols : 0;
        if (rows != retained.Rows || cols != retained.Cols || rows <= 0)
            return false;

        var covered = new bool[rows];
        foreach (var slice in slices)
        {
            if (slice.RowStart < 0 || slice.RowEnd < slice.RowStart || slice.RowEnd > rows)
                return false;
            for (var r = slice.RowStart; r < slice.RowEnd && r < rows; r++)
                covered[r] = true;
        }

        var grid = CloneCells(retained.Cells, rows, cols);
        foreach (var slice in slices)
            CopySliceRows(grid, slice, rows, cols);

        assembled = retained with
        {
            Cells = grid,
            Snapshot = last.Snapshot.ValueKind == JsonValueKind.Object ? last.Snapshot : retained.Snapshot,
            Cursor = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadCursor(last.Snapshot)
                : retained.Cursor,
            OccupantGeneration = last.OccupantGeneration > 0
                ? last.OccupantGeneration
                : retained.OccupantGeneration,
            Generation = last.Generation > 0 ? last.Generation : retained.Generation,
            SynchronizedOutput = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadSync(last.Snapshot)
                : retained.SynchronizedOutput,
            BracketedPaste = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadModeFlag(last.Snapshot, "bracketed_paste")
                : retained.BracketedPaste,
            ApplicationCursor = last.Snapshot.ValueKind == JsonValueKind.Object
                ? ReadModeFlag(last.Snapshot, "application_cursor")
                : retained.ApplicationCursor,
        };
        _ = covered;
        return true;
    }

    private static AssembledCell[][] CloneCells(
        IReadOnlyList<IReadOnlyList<AssembledCell>> source,
        int rows,
        int cols)
    {
        var grid = new AssembledCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            grid[r] = new AssembledCell[cols];
            var row = r < source.Count ? source[r] : null;
            for (var c = 0; c < cols; c++)
                grid[r][c] = row is not null && c < row.Count ? row[c] : AssembledCell.Blank;
        }

        return grid;
    }

    private static void CopySliceRows(
        AssembledCell[][] grid,
        TerminalRenderSnapshotPayload slice,
        int rows,
        int cols)
    {
        if (slice.Snapshot.ValueKind != JsonValueKind.Object)
            return;
        if (!slice.Snapshot.TryGetProperty("cells", out var cellsEl)
            || cellsEl.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var localRow = 0;
        foreach (var rowEl in cellsEl.EnumerateArray())
        {
            var destRow = slice.RowStart + localRow;
            localRow++;
            if (destRow < 0 || destRow >= rows || destRow >= grid.Length || rowEl.ValueKind != JsonValueKind.Array)
                continue;

            var col = 0;
            foreach (var cellEl in rowEl.EnumerateArray())
            {
                if (col >= cols)
                    break;
                grid[destRow][col] = AssembledCell.FromJson(cellEl);
                col++;
            }
        }
    }
}

public sealed record AssembledSnapshot(
    string PaneId,
    int Cols,
    int Rows,
    string Provider,
    string ActiveScreen,
    IReadOnlyList<IReadOnlyList<AssembledCell>> Cells,
    JsonElement Snapshot,
    AssembledCursor Cursor,
    int OccupantGeneration = 0,
    long Generation = 0,
    bool SynchronizedOutput = false,
    string? Mouse = null,
    string? MouseEncoding = null,
    bool IgnoreSnapshotMouse = false,
    bool IngestFull = false,
    bool BracketedPaste = false,
    bool ApplicationCursor = false)
{
    /// <summary>
    // / Row-major value cells.
    /// Empty when the snapshot was built from a test jagged list.
    /// </summary>
    internal AssembledCell[] Storage { get; init; } = [];

    public bool IsAlternateScreen => IsAlternateScreenName(ActiveScreen);

    public static bool IsAlternateScreenName(string? screen) =>
        string.Equals(screen, "alternate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(screen, "alt", StringComparison.OrdinalIgnoreCase);

    public AssembledCell CellAt(int col, int row)
    {
        if (Storage is { Length: > 0 } storage && Cols > 0)
        {
            if ((uint)col >= (uint)Cols || (uint)row >= (uint)Rows)
                return AssembledCell.Blank;
            return storage[row * Cols + col];
        }

        if ((uint)row < (uint)Cells.Count)
        {
            var line = Cells[(int)row];
            if ((uint)col < (uint)line.Count)
                return line[(int)col];
        }

        return AssembledCell.Blank;
    }
}

public sealed record AssembledCursor(int Col, int Row, bool Visible, int Shape = 0, bool HasCursor = true)
{
    /// <summary>
    // Missing snapshot cursor
    /// and HAS_VALUE false use this. Distinct from Some hidden at origin.
    /// </summary>
    public static AssembledCursor Default { get; } = new(0, 0, false, 0, HasCursor: false);
}

public readonly record struct AssembledCell(
    string Text,
    int Width,
    bool IsContinuation,
    AssembledStyle Style)
{
    private static readonly string[] Ascii = CreateAscii();

    public static AssembledCell Blank { get; } = new(" ", 1, false, AssembledStyle.Default);

    public static string InternText(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
            return " ";
        if (text.Length == 1 && (uint)text[0] < 128u)
            return Ascii[text[0]];
        return text.ToString();
    }

    public static AssembledCell FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return Blank;

        var text = " ";
        if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var raw = t.GetString();
            var safe = SafeDisplayText.Encode(raw);
            text = safe.Length > 0 ? InternText(safe.AsSpan()) : " ";
        }

        var width = 1;
        if (el.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) && wi > 0)
            width = wi;

        var cont = el.TryGetProperty("is_continuation", out var c)
            && c.ValueKind == JsonValueKind.True;

        var style = AssembledStyle.Default;
        if (el.TryGetProperty("style", out var st) && st.ValueKind == JsonValueKind.Object)
            style = AssembledStyle.FromJson(st);

        return new AssembledCell(text, width, cont, style);
    }

    private static string[] CreateAscii()
    {
        var items = new string[128];
        for (var i = 0; i < items.Length; i++)
            items[i] = char.ToString((char)i);
        return items;
    }
}

public readonly record struct AssembledStyle(
    uint Fg,
    uint Bg,
    bool Bold,
    bool Dim,
    bool Italic,
    bool Underline,
    bool Inverse,
    bool Invisible,
    bool Strikethrough,
    bool Blink = false,
    bool Overline = false,
    uint UnderlineColor = 0,
    int UnderlineStyle = 0,
    string? Hyperlink = null)
{
    public static AssembledStyle Default { get; } = new(
        0, 0, false, false, false, false, false, false, false);

    public bool IsDefault =>
        Fg == 0
        && Bg == 0
        && !Bold && !Dim && !Italic && !Underline && !Inverse && !Invisible && !Strikethrough
        && !Blink && !Overline && UnderlineColor == 0 && UnderlineStyle == 0
        && string.IsNullOrEmpty(Hyperlink);

    public static AssembledStyle FromJson(JsonElement el)
    {
        return new AssembledStyle(
            ReadPacked(el, "fg"),
            ReadPacked(el, "bg"),
            ReadBool(el, "bold"),
            ReadBool(el, "dim"),
            ReadBool(el, "italic"),
            ReadBool(el, "underline"),
            ReadBool(el, "inverse"),
            ReadBool(el, "invisible"),
            ReadBool(el, "strikethrough"),
            ReadBool(el, "blink"),
            ReadBool(el, "overline"),
            ReadPacked(el, "underline_color"),
            ReadInt(el, "underline_style"),
            ReadString(el, "hyperlink"));
    }

    public CellSgr ToSgr() =>
        new(
            Fg,
            Bg,
            Bold,
            Dim,
            Italic,
            Underline,
            Inverse,
            Invisible,
            Strikethrough,
            Blink,
            Overline,
            UnderlineColor,
            UnderlineStyle,
            Hyperlink);

    private static uint ReadPacked(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
            return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out var packed))
            return packed;
        if (p.ValueKind == JsonValueKind.String)
            return VtColorPack.Parse(p.GetString());
        return 0;
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool ReadBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static int ReadInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;
}
