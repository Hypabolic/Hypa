using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Reverse-video block at the focused cursor cell. Host cursor stays hidden.</summary>
public static class DrawnCursorPainter
{
    public const string ReverseOn = "\u001b[7m";
    public const string ReverseOff = "\u001b[27m";

    public static string Paint(CellRect box, int col, int row, string? glyph = null)
    {
        if (box.Cols < 1 || box.Rows < 1)
            return SnapshotPainter.HideCursor;

        var x = Math.Clamp(col, 0, box.Cols - 1);
        var y = Math.Clamp(row, 0, box.Rows - 1);
        var cell = SafeDisplayText.Clip(string.IsNullOrEmpty(glyph) ? " " : glyph, 1);
        if (cell.Length == 0)
            cell = " ";

        var sb = new StringBuilder(24);
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.CursorAddress(box.Col + x, box.Row + y));
        sb.Append(ReverseOn);
        sb.Append(cell);
        sb.Append(ReverseOff);
        return sb.ToString();
    }

    public static string Restore(CellRect box, int col, int row, string? glyph = null)
    {
        if (box.Cols < 1 || box.Rows < 1)
            return SnapshotPainter.HideCursor;

        var x = Math.Clamp(col, 0, box.Cols - 1);
        var y = Math.Clamp(row, 0, box.Rows - 1);
        var cell = SafeDisplayText.Clip(string.IsNullOrEmpty(glyph) ? " " : glyph, 1);
        if (cell.Length == 0)
            cell = " ";

        var sb = new StringBuilder(24);
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.CursorAddress(box.Col + x, box.Row + y));
        sb.Append(ReverseOff);
        sb.Append(cell);
        return sb.ToString();
    }

    internal static void Stamp(HostFrame frame, CellRect box, int col, int row, string? glyph = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (box.Cols < 1 || box.Rows < 1)
            return;
        var x = Math.Clamp(col, 0, box.Cols - 1);
        var y = Math.Clamp(row, 0, box.Rows - 1);
        var cell = SafeDisplayText.Clip(string.IsNullOrEmpty(glyph) ? " " : glyph, 1);
        if (cell.Length == 0)
            cell = " ";
        var existing = frame.CellAt(box.Col + x, box.Row + y);
        var style = existing.Style with { Inverse = true };
        frame.Stamp(box.Col + x, box.Row + y, new AssembledCell(cell, 1, false, style));
    }
}
