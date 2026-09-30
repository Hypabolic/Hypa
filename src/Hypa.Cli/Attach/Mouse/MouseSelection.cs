using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Copy;

namespace Hypa.Cli.Attach.Mouse;

/// <summary>Cell selection on the history or snapshot grid. Does not enter copy mode.</summary>
public sealed class MouseSelection
{
    public CopyModeBuffer Buffer { get; } = new();

    public string? PaneId { get; private set; }

    public int AnchorRow { get; private set; }

    public int AnchorCol { get; private set; }

    public int EndRow { get; private set; }

    public int EndCol { get; private set; }

    public bool Active { get; private set; }

    /// <summary>
    /// Host-cell range on the composed frame. Pane <see cref="Buffer"/>
    /// stays unused for this gesture.
    /// </summary>
    public bool HostRange { get; private set; }

    public bool IsEmpty => !Active || (!HostRange && Buffer.IsEmpty);

    public bool Dragged =>
        Active && (AnchorCol != EndCol || AnchorRow != EndRow);

    public void Seed(AssembledSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PaneId = snapshot.PaneId;
        var lines = CopyModeSession.ScreenLines(snapshot);
        Buffer.ReplaceAll(lines, Math.Max(1, snapshot.Cols), historyLineCount: 0, Math.Max(1, snapshot.Rows));
        Active = false;
    }

    public void Seed(CopyModeBuffer source, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(source);
        Seed(source.CapturePaintSnapshot(), paneId);
    }

    public void Seed(CopyModePaintSnapshot source, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(source);
        PaneId = paneId;
        var copy = new List<IReadOnlyList<CopyCell>>(source.RowCount);
        foreach (var line in source.Lines)
            copy.Add(line.ToList());
        Buffer.ReplaceAll(copy, Math.Max(1, source.Cols), historyLineCount: 0, Math.Max(1, source.ViewportRows));
        Buffer.SetViewportTop(source.ViewportTop);
        Active = false;
    }

    public void SeedText(string text, int cols, string? paneId = null)
    {
        PaneId = paneId;
        cols = Math.Max(1, cols);
        var lines = new List<IReadOnlyList<CopyCell>>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            lines.AddRange(CopyModeBuffer.WrapFromText(line, cols));
        if (lines.Count == 0)
            lines.Add(CopyModeBuffer.BlankLine(cols));
        Buffer.ReplaceAll(lines, cols, 0, Math.Max(1, lines.Count));
        Active = false;
    }

    public void Begin(int row, int col)
    {
        HostRange = false;
        ClampCell(row, col, out var r, out var c);
        AnchorRow = r;
        AnchorCol = c;
        EndRow = r;
        EndCol = c;
        Active = true;
    }

    public void BeginHost(int col, int row)
    {
        AnchorCol = EndCol = Math.Max(0, col);
        AnchorRow = EndRow = Math.Max(0, row);
        HostRange = true;
        Active = true;
        PaneId = null;
    }

    public void ExtendHost(int col, int row)
    {
        if (!Active || !HostRange)
            BeginHost(col, row);
        EndCol = Math.Max(0, col);
        EndRow = Math.Max(0, row);
    }

    public bool ContainsHost(int col, int row)
    {
        if (!Active || !HostRange)
            return false;
        HostFrame.NormalizeHostRange(
            AnchorCol,
            AnchorRow,
            EndCol,
            EndRow,
            out var c1,
            out var r1,
            out var c2,
            out var r2);
        if (row < r1 || row > r2)
            return false;
        if (row == r1 && col < c1)
            return false;
        if (row == r2 && col > c2)
            return false;
        return true;
    }

    internal string ExtractHost(HostFrame host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!Active || !HostRange)
            return "";
        return host.ExtractRange(AnchorCol, AnchorRow, EndCol, EndRow);
    }

    public void Extend(int row, int col)
    {
        if (!Active)
            Begin(row, col);
        ClampCell(row, col, out var r, out var c);
        EndRow = r;
        EndCol = c;
    }

    public bool SelectWord(int row, int col)
    {
        ClampCell(row, col, out var r, out var c);
        if (!CopyMotions.TryWordBounds(Buffer, r, c, out var start, out var end))
        {
            Begin(r, c);
            return false;
        }

        AnchorRow = r;
        AnchorCol = start;
        EndRow = r;
        EndCol = end;
        Active = true;
        return true;
    }

    public string Extract()
    {
        if (!Active)
            return "";
        return Buffer.Extract(AnchorRow, AnchorCol, EndRow, EndCol);
    }

    public void Clear()
    {
        Active = false;
        HostRange = false;
        PaneId = null;
        Buffer.Clear();
    }

    public string Paint(CellRect box)
    {
        if (!Active || Buffer.IsEmpty)
            return "";
        CopyModeBuffer.Normalize(AnchorRow, AnchorCol, EndRow, EndCol, out var r1, out var c1, out var r2, out var c2);
        var sb = new StringBuilder();
        sb.Append('\u001b').Append('7');
        var top = Math.Max(0, Buffer.ViewportTop);
        var rows = Math.Min(box.Rows, Math.Max(0, Buffer.RowCount - top));
        for (var i = 0; i < rows; i++)
        {
            var srcRow = top + i;
            var line = Buffer.Lines[srcRow];
            sb.Append(SnapshotPainter.CursorAddress(box.Col, box.Row + i));
            var written = 0;
            for (var c = 0; c < line.Count && written < box.Cols; c++)
            {
                var cell = line[c];
                if (cell.IsContinuation)
                    continue;
                var selected = InRange(srcRow, c, r1, c1, r2, c2);
                if (selected)
                    sb.Append("\u001b[7m");
                var text = SafeDisplayText.Encode(cell.Text);
                sb.Append(text.Length > 0 ? text : " ");
                if (selected)
                    sb.Append(SnapshotPainter.ResetSgr);
                written += cell.Width > 0 ? cell.Width : 1;
            }
        }

        sb.Append('\u001b').Append('8');
        return sb.ToString();
    }

    private void ClampCell(int row, int col, out int r, out int c)
    {
        if (Buffer.IsEmpty)
        {
            r = 0;
            c = 0;
            return;
        }

        r = Math.Clamp(row, 0, Buffer.RowCount - 1);
        c = Math.Clamp(col, 0, Math.Max(0, Buffer.LineLength(r) - 1));
        if (Buffer.IsContinuation(r, c))
        {
            var prev = Buffer.PrevPrimaryCol(r, c);
            c = prev >= 0 ? prev : Buffer.FirstPrimaryCol(r);
        }
    }

    private static bool InRange(int row, int col, int r1, int c1, int r2, int c2)
    {
        if (row < r1 || row > r2)
            return false;
        if (row == r1 && col < c1)
            return false;
        if (row == r2 && col > c2)
            return false;
        return true;
    }
}
