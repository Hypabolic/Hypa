using System.Runtime.CompilerServices;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Canonical semantic pane frame. Compared before encode or send.
/// Self-contained: a replaced pending value never depends on a dropped patch.
// / The grid is one flat row-major packed cell array.
/// <c>src/protocol/wire.rs:731-744</c> <c>FrameData</c> holds
/// <c>cells: Vec&lt;CellData&gt;</c> in row-major order with
/// <c>width</c> and <c>height</c>.
/// </summary>
public sealed record VtFrame(
    string PaneId,
    int Cols,
    int Rows,
    VtFrameGrid Grid,
    VtFrameCursor Cursor,
    VtFrameModes Modes,
    int ViewportOrigin,
    int OccupantGeneration,
    long Generation,
    string Provider = "ghostty",
    string ActiveScreen = "main")
{
    public VtFrame(
        string paneId,
        int cols,
        int rows,
        IReadOnlyList<IReadOnlyList<VtCellView>> cells,
        VtFrameCursor cursor,
        VtFrameModes modes,
        int viewportOrigin,
        int occupantGeneration,
        long generation,
        string provider = "ghostty",
        string activeScreen = "main")
        : this(paneId, cols, rows, PackViews(cols, rows, cells), cursor, modes, viewportOrigin, occupantGeneration, generation, provider, activeScreen)
    {
    }

    private static VtFrameGrid PackViews(int cols, int rows, IReadOnlyList<IReadOnlyList<VtCellView>> cells)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var tables = new VtStampTables();
        var packed = new VtFrameCell[cols * rows];
        for (var r = 0; r < rows; r++)
        {
            var row = r < cells.Count ? cells[r] : null;
            for (var c = 0; c < cols; c++)
            {
                var view = row is not null && c < row.Count ? row[c] : VtCellView.Blank;
                packed[(r * cols) + c] = view.Pack(tables);
            }
        }

        return new VtFrameGrid(packed, tables.Freeze(null), cols, rows);
    }

    public bool CellsVisuallyEqual(VtFrame other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Cols != other.Cols || Rows != other.Rows)
            return false;
        if (ViewportOrigin != other.ViewportOrigin)
            return false;
        if (OccupantGeneration != other.OccupantGeneration)
            return false;
        if (!Cursor.Equals(other.Cursor) || !Modes.Equals(other.Modes))
            return false;
        if (!string.Equals(ActiveScreen, other.ActiveScreen, StringComparison.Ordinal))
            return false;
        if (ReferenceEquals(Grid, other.Grid))
            return true;
        // Fast path: same frozen tables instance, so equal indexes mean
        // equal content. The stamp order is row-major and stable.
        if (ReferenceEquals(Grid.Tables, other.Grid.Tables))
            return Grid.Cells.AsSpan().SequenceEqual(other.Grid.Cells.AsSpan());
        // Slow path: compare resolved views cell by cell.
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                if (!CellAt(r, c).VisuallyEquals(other.CellAt(r, c)))
                    return false;
            }
        }

        return true;
    }

    public VtCellView CellAt(int row, int col) => Grid.ViewAt(row, col);

    public VtFrameCell PackedAt(int row, int col) => Grid.PackedAt(row, col);

    /// <summary>
    /// Transient row views over the flat grid. Convenience for readers and
    /// tests. Never retained beyond the call.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<VtCellView>> Cells => new VtViewRows(Grid);
}

/// <summary>
/// Dirty-row capture input. Never a replaceable wire value. Apply onto a
/// complete last-admitted <see cref="VtFrame"/> before encode.
/// </summary>
public sealed record VtDirtyRowPatch(
    int Cols,
    int Rows,
    VtFrameCursor Cursor,
    VtFrameModes Modes,
    int ViewportOrigin,
    string ActiveScreen,
    string Provider,
    IReadOnlyList<VtDirtyRow> ChangedRows,
    VtFrameTables? Tables = null)
{
    public IReadOnlyList<int> RowIndices
    {
        get
        {
            var indices = new int[ChangedRows.Count];
            for (var i = 0; i < ChangedRows.Count; i++)
                indices[i] = ChangedRows[i].Index;
            return indices;
        }
    }
}

public readonly record struct VtDirtyRow(int Index, VtFrameCell[] Cells, VtFrameTables? Tables = null);

public readonly record struct VtFrameCursor(int Col, int Row, bool Visible, int Shape, bool HasCursor = true)
{
    /// <summary>
    /// suppress. Distinct from Some at origin with <c>visible: false</c>.
    /// </summary>
    public static VtFrameCursor None { get; } = new(0, 0, false, 0, false);
}

public readonly record struct VtFrameModes(
    bool Origin,
    bool AutoWrap,
    bool Insert,
    bool BracketedPaste,
    string Mouse,
    bool FocusReporting,
    bool SynchronizedOutput = false,
    string? MouseEncoding = null,
    bool ApplicationCursor = false);

/// <summary>
/// One 64-bit retained cell. Packs a glyph index, a style index, the
// / display width, and the continuation bit.
/// <c>src/protocol/wire.rs:680-693</c> <c>CellData</c> holds a symbol, a
/// <c>u32</c> foreground, a <c>u32</c> background, a <c>u16</c> modifier, a
/// skip flag, and an <c>Option&lt;u32&gt;</c> hyperlink index into
/// <c>FrameData::hyperlinks</c>. Hypa holds indexes into per-frame tables.
/// The hyperlink field uses index plus one so that a default cell is zero.
/// </summary>
public readonly record struct VtFrameCell(ulong Packed)
{
    public const int MaxGlyphIndex = (1 << 24) - 1;
    public const int MaxStyleIndex = (1 << 16) - 1;
    public const int MaxHyperlinkIndex = (1 << 20) - 2;

    private const ulong GlyphMask = 0xFFFFFFul;
    private const ulong StyleMask = 0xFFFFul << 24;
    private const ulong HyperlinkMask = 0xFFFFFul << 40;
    private const ulong WidthMask = 0x3ul << 60;
    private const ulong ContinuationMask = 0x1ul << 62;

    public static VtFrameCell Blank { get; } = Pack(32, 0, -1, 1, false);

    public int GlyphIndex => (int)(Packed & GlyphMask);

    public int StyleIndex => (int)((Packed >> 24) & 0xFFFFul);

    /// <summary>Hyperlink table index, or -1 when none.</summary>
    public int HyperlinkIndex
    {
        get
        {
            var raw = (int)((Packed >> 40) & 0xFFFFFul);
            return raw == 0 ? -1 : raw - 1;
        }
    }

    public int Width => (int)((Packed >> 60) & 0x3ul);

    public bool IsContinuation => (Packed & ContinuationMask) != 0;

    public static VtFrameCell Pack(int glyphIndex, int styleIndex, int hyperlinkIndex, int width, bool isContinuation)
    {
        if ((uint)glyphIndex > (uint)MaxGlyphIndex)
            glyphIndex = 32;
        if ((uint)styleIndex > (uint)MaxStyleIndex)
            styleIndex = 0;
        var link = hyperlinkIndex < 0 ? 0 : hyperlinkIndex + 1;
        if ((uint)link > (uint)(MaxHyperlinkIndex + 1))
            link = 0;
        width = Math.Clamp(width, 0, 3);
        var packed = (ulong)(uint)glyphIndex
            | ((ulong)(uint)styleIndex << 24)
            | ((ulong)(uint)link << 40)
            | ((ulong)(uint)width << 60)
            | (isContinuation ? ContinuationMask : 0ul);
        return new VtFrameCell(packed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VtCellView Unpack(VtFrameTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables.Resolve(in this);
    }
}

/// <summary>
/// Transient resolved cell view. Never retained. Produced on read from a
/// packed cell plus the frame tables. Keeps the members that
/// <c>VtFrameCell</c> held before the pack, with the same convenience
/// constructor and the same <c>VisuallyEquals</c> method.
/// </summary>
public readonly record struct VtCellView(
    string Text,
    int Width,
    bool IsContinuation,
    uint FgPacked,
    uint BgPacked,
    ushort Modifier,
    uint UnderlineColorPacked = 0,
    byte UnderlineStylePacked = 0,
    string? Hyperlink = null,
    ushort StyleIndex = 0)
{
    public VtCellView(
        string text,
        int width,
        bool isContinuation,
        string? fg,
        string? bg,
        bool bold,
        bool dim,
        bool italic,
        bool underline,
        bool inverse,
        bool invisible,
        bool strikethrough,
        bool blink = false,
        bool overline = false,
        string? underlineColor = null,
        int underlineStyle = 0,
        string? Hyperlink = null)
        : this(
            VtGlyphIntern.Intern(string.IsNullOrEmpty(text) ? " " : text),
            width,
            isContinuation,
            VtColorPack.Parse(fg),
            VtColorPack.Parse(bg),
            VtStyleBits.Pack(bold, dim, italic, underline, inverse, invisible, strikethrough, blink, overline),
            VtColorPack.Parse(underlineColor),
            (byte)Math.Clamp(underlineStyle, 0, 255),
            Hyperlink,
            0)
    {
    }

    public static VtCellView Blank { get; } = new(
        VtGlyphIntern.Space, 1, false, 0u, 0u, (ushort)0);

    public string? Fg
    {
        get => VtColorPack.ToWire(FgPacked);
        init => FgPacked = VtColorPack.Parse(value);
    }

    public string? Bg
    {
        get => VtColorPack.ToWire(BgPacked);
        init => BgPacked = VtColorPack.Parse(value);
    }

    public string? UnderlineColor
    {
        get => VtColorPack.ToWire(UnderlineColorPacked);
        init => UnderlineColorPacked = VtColorPack.Parse(value);
    }

    public bool Bold => (Modifier & VtStyleBits.Bold) != 0;
    public bool Dim => (Modifier & VtStyleBits.Dim) != 0;
    public bool Italic => (Modifier & VtStyleBits.Italic) != 0;
    public bool Underline => (Modifier & VtStyleBits.Underline) != 0;
    public bool Inverse => (Modifier & VtStyleBits.Inverse) != 0;
    public bool Invisible => (Modifier & VtStyleBits.Invisible) != 0;
    public bool Strikethrough => (Modifier & VtStyleBits.Strikethrough) != 0;
    public bool Blink => (Modifier & VtStyleBits.Blink) != 0;
    public bool Overline => (Modifier & VtStyleBits.Overline) != 0;
    public int UnderlineStyle => UnderlineStylePacked;

    public bool VisuallyEquals(VtCellView other) =>
        Text == other.Text
        && Width == other.Width
        && IsContinuation == other.IsContinuation
        && FgPacked == other.FgPacked
        && BgPacked == other.BgPacked
        && Modifier == other.Modifier
        && UnderlineColorPacked == other.UnderlineColorPacked
        && UnderlineStylePacked == other.UnderlineStylePacked
        && Hyperlink == other.Hyperlink;

    public VtFrameCell Pack(VtStampTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var style = new VtPackedStyle(FgPacked, BgPacked, UnderlineColorPacked, Modifier, UnderlineStylePacked);
        return tables.PackCell(Text, Width, IsContinuation, in style, Hyperlink);
    }
}

/// <summary>Pure changed-cell encode. Does not mutate the admitted baseline.</summary>
public readonly record struct VtBlitEncodeResult(
    string Ansi,
    bool Full,
    int ChangedCells,
    int WireBytes);
