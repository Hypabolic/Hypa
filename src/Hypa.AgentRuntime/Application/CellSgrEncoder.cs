using System.Buffers;
using System.Globalization;
using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Shared SGR and OSC 8 writer for blit and history paint.
/// Join parameters with a semicolon flag. Do not allocate a list per style.
/// </summary>
public static class CellSgrEncoder
{
    public static CellSgr From(in VtCellView cell) =>
        new(
            cell.FgPacked,
            cell.BgPacked,
            cell.Bold,
            cell.Dim,
            cell.Italic,
            cell.Underline,
            cell.Inverse,
            cell.Invisible,
            cell.Strikethrough,
            cell.Blink,
            cell.Overline,
            cell.UnderlineColorPacked,
            cell.UnderlineStyle,
            cell.Hyperlink);

    public static void AppendSgr(StringBuilder sb, CellSgr style)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (style.IsDefault)
            return;
        Span<char> buf = stackalloc char[128];
        var n = WriteSgrParams(style, buf);
        if (n == 0)
            return;
        sb.Append("\u001b[");
        sb.Append(buf[..n]);
        sb.Append('m');
    }

    public static void AppendSgr(IBufferWriter<byte> writer, CellSgr style)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (style.IsDefault)
            return;
        Span<char> buf = stackalloc char[128];
        var n = WriteSgrParams(style, buf);
        if (n == 0)
            return;
        writer.Write("\u001b["u8);
        WriteAscii(writer, buf[..n]);
        writer.Write("m"u8);
    }

    public static string SanitizeHyperlink(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return "";
        var sb = new StringBuilder(uri.Length);
        foreach (var ch in uri)
        {
            if (ch is '\u001b' or '\u0007' || char.IsControl(ch))
                continue;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    public static void WriteHyperlinkIfChanged(StringBuilder sb, ref string? active, string? next)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (!TryAdvanceHyperlink(ref active, next, out var closing, out var opening))
            return;
        if (closing)
            sb.Append("\u001b]8;;\u001b\\");
        if (opening is not null)
        {
            sb.Append("\u001b]8;;");
            sb.Append(opening);
            sb.Append("\u001b\\");
        }
    }

    public static void WriteHyperlinkIfChanged(IBufferWriter<byte> writer, ref string? active, string? next)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!TryAdvanceHyperlink(ref active, next, out var closing, out var opening))
            return;
        if (closing)
            writer.Write("\u001b]8;;\u001b\\"u8);
        if (opening is not null)
        {
            writer.Write("\u001b]8;;"u8);
            WriteUtf8(writer, opening);
            writer.Write("\u001b\\"u8);
        }
    }

    public static void CloseHyperlink(StringBuilder sb, ref string? active)
    {
        ArgumentNullException.ThrowIfNull(sb);
        if (string.IsNullOrEmpty(active))
            return;
        sb.Append("\u001b]8;;\u001b\\");
        active = null;
    }

    public static void CloseHyperlink(IBufferWriter<byte> writer, ref string? active)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (string.IsNullOrEmpty(active))
            return;
        writer.Write("\u001b]8;;\u001b\\"u8);
        active = null;
    }

    private static bool TryAdvanceHyperlink(
        ref string? active,
        string? next,
        out bool closing,
        out string? opening)
    {
        closing = false;
        opening = null;
        var sanitized = SanitizeHyperlink(next);
        next = sanitized.Length == 0 ? null : sanitized;
        if (string.Equals(active, next, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(active))
        {
            closing = true;
            active = null;
            if (string.IsNullOrEmpty(next))
                return true;
        }

        if (!string.IsNullOrEmpty(next))
        {
            opening = next;
            active = next;
        }

        return closing || opening is not null;
    }

    private static int WriteSgrParams(CellSgr style, Span<char> dest)
    {
        var join = new SgrJoin(dest);
        if (style.Bold)
            join.Add(1);
        if (style.Dim)
            join.Add(2);
        if (style.Italic)
            join.Add(3);
        if (style.UnderlineStyle is >= 2 and <= 5)
            join.AddUnderline(style.UnderlineStyle);
        else if (style.Underline || style.UnderlineStyle == 1)
            join.Add(4);
        if (style.Blink)
            join.Add(5);
        if (style.Strikethrough)
            join.Add(9);
        if (style.Overline)
            join.Add(53);
        if (style.Inverse)
            join.Add(7);
        if (style.Invisible)
            join.Add(8);
        AppendColor(ref join, style.Fg, foreground: true);
        AppendColor(ref join, style.Bg, foreground: false);
        AppendColor(ref join, style.UnderlineColor, foreground: true, underline: true);
        return join.Length;
    }

    private static void AppendColor(
        ref SgrJoin join,
        uint color,
        bool foreground,
        bool underline = false)
    {
        if (color == 0)
            return;
        var tag = color >> 24;
        if (tag == 0x01)
        {
            join.Add(underline ? 58 : foreground ? 38 : 48);
            join.Add(5);
            join.Add((int)(color & 0xFF));
            return;
        }

        if (tag == 0x02)
        {
            join.Add(underline ? 58 : foreground ? 38 : 48);
            join.Add(2);
            join.Add((int)((color >> 16) & 0xFF));
            join.Add((int)((color >> 8) & 0xFF));
            join.Add((int)(color & 0xFF));
            return;
        }

        if (tag != 0x00)
            return;
        var named = color & 0xFF;
        if (underline)
        {
            if (named is >= 1 and <= 16)
            {
                join.Add(58);
                join.Add(5);
                join.Add((int)named);
            }

            return;
        }

        var sgr = NamedSgr(named, foreground);
        if (sgr >= 0)
            join.Add(sgr);
    }

    private static int NamedSgr(uint named, bool foreground) =>
        named switch
        {
            0x01 => foreground ? 30 : 40,
            0x02 => foreground ? 31 : 41,
            0x03 => foreground ? 32 : 42,
            0x04 => foreground ? 33 : 43,
            0x05 => foreground ? 34 : 44,
            0x06 => foreground ? 35 : 45,
            0x07 => foreground ? 36 : 46,
            0x08 => foreground ? 37 : 47,
            0x09 => foreground ? 90 : 100,
            0x0A => foreground ? 91 : 101,
            0x0B => foreground ? 92 : 102,
            0x0C => foreground ? 93 : 103,
            0x0D => foreground ? 94 : 104,
            0x0E => foreground ? 95 : 105,
            0x0F => foreground ? 96 : 106,
            0x10 => foreground ? 97 : 107,
            _ => -1,
        };

    private static void WriteAscii(IBufferWriter<byte> writer, ReadOnlySpan<char> ascii)
    {
        var dest = writer.GetSpan(ascii.Length);
        for (var i = 0; i < ascii.Length; i++)
            dest[i] = (byte)ascii[i];
        writer.Advance(ascii.Length);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string text)
    {
        var max = Encoding.UTF8.GetMaxByteCount(text.Length);
        var dest = writer.GetSpan(max);
        var n = Encoding.UTF8.GetBytes(text, dest);
        writer.Advance(n);
    }

    private ref struct SgrJoin
    {
        private readonly Span<char> _dest;
        private int _written;
        private bool _started;

        public SgrJoin(Span<char> dest)
        {
            _dest = dest;
            _written = 0;
            _started = false;
        }

        public int Length => _written;

        public void Add(int value)
        {
            Sep();
            if (!value.TryFormat(_dest[_written..], out var n, provider: CultureInfo.InvariantCulture))
                throw new InvalidOperationException("sgr param");
            _written += n;
        }

        public void AddUnderline(int style)
        {
            Sep();
            if (_written + 2 >= _dest.Length)
                throw new InvalidOperationException("sgr param");
            _dest[_written++] = '4';
            _dest[_written++] = ':';
            if (!style.TryFormat(_dest[_written..], out var n, provider: CultureInfo.InvariantCulture))
                throw new InvalidOperationException("sgr param");
            _written += n;
        }

        private void Sep()
        {
            if (_started)
            {
                if (_written >= _dest.Length)
                    throw new InvalidOperationException("sgr param");
                _dest[_written++] = ';';
            }

            _started = true;
        }
    }
}

public readonly record struct CellSgr(
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
    public bool IsDefault =>
        Fg == 0
        && Bg == 0
        && !Bold && !Dim && !Italic && !Underline && !Inverse && !Invisible && !Strikethrough
        && !Blink && !Overline && UnderlineColor == 0 && UnderlineStyle == 0
        && string.IsNullOrEmpty(Hyperlink);
}
