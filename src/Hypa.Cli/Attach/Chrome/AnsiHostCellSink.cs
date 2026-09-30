using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>ANSI CUP sink for overlay painters that still write bytes.</summary>
internal sealed class AnsiHostCellSink : IHostCellSink
{
    private readonly StringBuilder _sb;

    public AnsiHostCellSink(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        _sb = sb;
    }

    public void Write(
        int col,
        int row,
        string text,
        ThemeColor? fg,
        ThemeColor? bg,
        int maxCols = -1,
        bool inverse = false,
        bool bold = false,
        bool dim = false)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (maxCols == 0)
            return;
        if (maxCols > 0 && SafeDisplayText.Width(text) > maxCols)
            text = SafeDisplayText.Clip(text, maxCols);
        if (string.IsNullOrEmpty(text))
            return;
        _sb.Append(SnapshotPainter.CursorAddress(col, row));
        if (inverse)
        {
            if (bold)
                _sb.Append("\u001b[1m");
            if (dim)
                _sb.Append("\u001b[2m");
            _sb.Append("\u001b[7m");
            _sb.Append(text);
            _sb.Append(SnapshotPainter.ResetSgr);
            return;
        }

        ThemeSgr.WriteStyled(_sb, fg, bg, text, bold, dim);
    }
}
