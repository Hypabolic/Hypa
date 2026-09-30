using System.Globalization;
using System.Text;
using Hypa.AgentRuntime.Domain.Theme;

namespace Hypa.Cli.Attach.Theme;

/// <summary>SGR for chrome tokens. Reset skips. Named ANSI uses 30–37 / 90–97.</summary>
public static class ThemeSgr
{
    public static string Fg(ThemeColor color) => Sequence(color, background: false);

    public static string Bg(ThemeColor color) => Sequence(color, background: true);

    public static string Pair(ThemeColor? fg, ThemeColor? bg)
    {
        var f = fg is { } fgColor ? FgCodes(fgColor) : null;
        var b = bg is { } bgColor ? BgCodes(bgColor) : null;
        if (f is null && b is null)
            return "";
        if (f is null)
            return "\u001b[" + b + "m";
        if (b is null)
            return "\u001b[" + f + "m";
        return "\u001b[" + f + ";" + b + "m";
    }

    public static void AppendFg(StringBuilder sb, ThemeColor color)
    {
        var seq = Fg(color);
        if (seq.Length > 0)
            sb.Append(seq);
    }

    public static void AppendBg(StringBuilder sb, ThemeColor color)
    {
        var seq = Bg(color);
        if (seq.Length > 0)
            sb.Append(seq);
    }

    public static void AppendPair(StringBuilder sb, ThemeColor? fg, ThemeColor? bg)
    {
        var seq = Pair(fg, bg);
        if (seq.Length > 0)
            sb.Append(seq);
    }

    public static void AppendReset(StringBuilder sb) => sb.Append(SnapshotPainter.ResetSgr);

    public static void WriteStyled(
        StringBuilder sb,
        ThemeColor? fg,
        ThemeColor? bg,
        string text,
        bool bold = false,
        bool dim = false)
    {
        if (bold)
            sb.Append("\u001b[1m");
        if (dim)
            sb.Append("\u001b[2m");
        var seq = Pair(fg, bg);
        if (seq.Length > 0)
            sb.Append(seq);
        sb.Append(text);
        if (seq.Length > 0 || bold || dim)
            sb.Append(SnapshotPainter.ResetSgr);
    }

    private static string Sequence(ThemeColor color, bool background)
    {
        var codes = background ? BgCodes(color) : FgCodes(color);
        return codes is null ? "" : "\u001b[" + codes + "m";
    }

    private static string? FgCodes(ThemeColor color) =>
        color.Kind switch
        {
            ThemeColorKind.Reset => null,
            ThemeColorKind.Ansi => AnsiCode(color.Ansi, background: false).ToString(CultureInfo.InvariantCulture),
            ThemeColorKind.Rgb => string.Create(CultureInfo.InvariantCulture, $"38;2;{color.R};{color.G};{color.B}"),
            _ => null,
        };

    private static string? BgCodes(ThemeColor color) =>
        color.Kind switch
        {
            ThemeColorKind.Reset => null,
            ThemeColorKind.Ansi => AnsiCode(color.Ansi, background: true).ToString(CultureInfo.InvariantCulture),
            ThemeColorKind.Rgb => string.Create(CultureInfo.InvariantCulture, $"48;2;{color.R};{color.G};{color.B}"),
            _ => null,
        };

    internal static int AnsiCode(ThemeAnsiColor ansi, bool background)
    {
        var offset = background ? 10 : 0;
        return ansi switch
        {
            ThemeAnsiColor.Black => 30 + offset,
            ThemeAnsiColor.Red => 31 + offset,
            ThemeAnsiColor.Green => 32 + offset,
            ThemeAnsiColor.Yellow => 33 + offset,
            ThemeAnsiColor.Blue => 34 + offset,
            ThemeAnsiColor.Magenta => 35 + offset,
            ThemeAnsiColor.Cyan => 36 + offset,
            ThemeAnsiColor.Gray => 37 + offset,
            ThemeAnsiColor.White => 97 + offset,
            ThemeAnsiColor.DarkGray => 90 + offset,
            ThemeAnsiColor.LightRed => 91 + offset,
            ThemeAnsiColor.LightGreen => 92 + offset,
            ThemeAnsiColor.LightYellow => 93 + offset,
            ThemeAnsiColor.LightBlue => 94 + offset,
            ThemeAnsiColor.LightMagenta => 95 + offset,
            ThemeAnsiColor.LightCyan => 96 + offset,
            _ => 39 + offset,
        };
    }
}
