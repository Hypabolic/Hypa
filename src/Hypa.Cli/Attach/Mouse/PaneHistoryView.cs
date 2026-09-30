using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Copy;

namespace Hypa.Cli.Attach.Mouse;

/// <summary>
/// Scrollback view for wheel and scrollbar. Reuses copy-mode seed rules.
/// Never sets <c>AttachClientMode.Copy</c>.
/// </summary>
public sealed class PaneHistoryView
{
    public CopyModeSession Session { get; } = new();

    public string? PaneId { get; private set; }

    public bool IsSeeded => Session.IsSeeded;

    public bool IsAltScreen => Session.IsAltScreen;

    public bool ShowsOverlay => Session.ShowsOverlay;

    public void Reset()
    {
        Session.Reset();
        PaneId = null;
    }

    public void Seed(AssembledSnapshot? snapshot, string? recentText, int viewportRows, string? paneId)
    {
        PaneId = paneId ?? snapshot?.PaneId;
        Session.Seed(snapshot, recentText, viewportRows);
    }

    public void AppendLive(ReadOnlySpan<byte> bytes)
    {
        if (!Session.IsSeeded || Session.IsAltScreen)
            return;
        Session.AppendLive(bytes);
    }

    public void Scroll(int deltaLines) => Session.ScrollLines(deltaLines);

    public void SetViewportTop(int top) => Session.SetViewportTop(top);

    public void SetViewportFromY(int y, CellRect scrollbar) =>
        Session.SetViewportFromY(y, scrollbar);

    public string Paint(CellRect box)
    {
        if (!Session.IsSeeded)
            return "";
        var view = Session.CapturePaintSnapshot(Math.Max(1, box.Rows));
        var sb = new StringBuilder();
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.EraseRect(box.Col, box.Row, box.Cols, box.Rows));
        var top = view.ViewportTop;
        AssembledStyle? current = null;
        string? activeHyperlink = null;
        for (var i = 0; i < box.Rows; i++)
        {
            var row = top + i;
            sb.Append(SnapshotPainter.CursorAddress(box.Col, box.Row + i));
            if (row >= view.RowCount)
            {
                CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                sb.Append(SnapshotPainter.ResetSgr);
                current = null;
                sb.Append(' ', box.Cols);
                continue;
            }

            var written = 0;
            var line = view.Lines[row];
            for (var c = 0; c < line.Count && written < box.Cols; c++)
            {
                var cell = line[c];
                if (cell.IsContinuation)
                    continue;
                if (current is not { } liveStyle || !StylesEqual(liveStyle, cell.Style))
                {
                    CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
                    sb.Append(SnapshotPainter.ResetSgr);
                    CellSgrEncoder.AppendSgr(sb, cell.Style.ToSgr());
                    current = cell.Style;
                }

                CellSgrEncoder.WriteHyperlinkIfChanged(sb, ref activeHyperlink, cell.Style.Hyperlink);
                var text = SafeDisplayText.Encode(cell.Text);
                sb.Append(text.Length > 0 ? text : " ");
                written += cell.Width > 0 ? cell.Width : 1;
            }

            if (written < box.Cols)
                sb.Append(' ', box.Cols - written);
        }

        CellSgrEncoder.CloseHyperlink(sb, ref activeHyperlink);
        sb.Append(SnapshotPainter.ResetSgr);
        return sb.ToString();
    }

    private static bool StylesEqual(AssembledStyle a, AssembledStyle b) =>
        a.Fg == b.Fg
        && a.Bg == b.Bg
        && a.Bold == b.Bold
        && a.Dim == b.Dim
        && a.Italic == b.Italic
        && a.Underline == b.Underline
        && a.Inverse == b.Inverse
        && a.Invisible == b.Invisible
        && a.Strikethrough == b.Strikethrough
        && a.Blink == b.Blink
        && a.Overline == b.Overline
        && a.UnderlineColor == b.UnderlineColor
        && a.UnderlineStyle == b.UnderlineStyle
        && a.Hyperlink == b.Hyperlink;
}
