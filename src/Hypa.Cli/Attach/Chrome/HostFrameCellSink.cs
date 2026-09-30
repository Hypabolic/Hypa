using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Stamps chrome glyphs into a host-size cell frame.</summary>
internal sealed class HostFrameCellSink : IHostCellSink
{
    private readonly HostFrame _frame;

    public HostFrameCellSink(HostFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
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
        var style = StyleOf(fg, bg, bold, dim);
        if (inverse)
            style = style with { Inverse = true };
        _frame.StampGlyphRun(col, row, text, style, maxCols);
    }

    public void DimAll() => _frame.DimAll();

    internal static AssembledStyle StyleOf(
        ThemeColor? fg,
        ThemeColor? bg,
        bool bold = false,
        bool dim = false) =>
        new(
            Encode(fg),
            Encode(bg),
            bold,
            dim,
            false,
            false,
            false,
            false,
            false);

    internal static uint Encode(ThemeColor? color)
    {
        if (color is not { } value || value.IsReset)
            return 0;
        if (value.IsRgb)
            return VtColorPack.FromRgb(value.R, value.G, value.B);
        if (!value.IsAnsi)
            return 0;
        return VtColorPack.FromNamed(NamedIndex(value.Ansi));
    }

    private static byte NamedIndex(ThemeAnsiColor ansi) =>
        ansi switch
        {
            ThemeAnsiColor.Black => 1,
            ThemeAnsiColor.Red => 2,
            ThemeAnsiColor.Green => 3,
            ThemeAnsiColor.Yellow => 4,
            ThemeAnsiColor.Blue => 5,
            ThemeAnsiColor.Magenta => 6,
            ThemeAnsiColor.Cyan => 7,
            ThemeAnsiColor.Gray => 8,
            ThemeAnsiColor.DarkGray => 9,
            ThemeAnsiColor.LightRed => 10,
            ThemeAnsiColor.LightGreen => 11,
            ThemeAnsiColor.LightYellow => 12,
            ThemeAnsiColor.LightBlue => 13,
            ThemeAnsiColor.LightMagenta => 14,
            ThemeAnsiColor.LightCyan => 15,
            ThemeAnsiColor.White => 16,
            _ => 8,
        };
}
